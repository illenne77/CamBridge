using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using CamBridge.Core;
using CamBridge.Sources.Webcam.Interop;
using OpenCvSharp;
using PixelFormat = CamBridge.Core.PixelFormat;

namespace CamBridge.Sources.Webcam;

/// <summary>
/// A UVC webcam captured through OpenCV's DirectShow backend, with exposure, gain and the other
/// UVC controls driven directly through IAMCameraControl / IAMVideoProcAmp.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="DeviceThread"/> owns the VideoCapture from open to close; stream changes
/// (open, resolution, start/stop) are marshalled to it. While acquiring it reads frames back to
/// back, which also keeps the driver's frame buffer from going stale.
/// </para>
/// <para>
/// UVC controls are called directly from the caller's thread under a lock instead: KS property
/// requests are safe while streaming, and routing them through the device thread would make
/// each access wait for the frame in progress (up to 250 ms at long exposures, ~10 s to read
/// the whole list). The filter lives in the MTA, so STA callers (e.g. a UI thread) still go
/// through the device thread to avoid needing a COM proxy.
/// </para>
/// <para>
/// Software trigger is emulated: the stream keeps running and the first frame whose read starts
/// after a trigger is published (latency up to one frame period). Webcams have no block ID, so
/// <see cref="Frame.FrameId"/> counts published frames and losses inside the driver are invisible.
/// </para>
/// <para>
/// Following SFNC, a control's value is writable only while its auto switch (e.g. ExposureAuto)
/// is Off. Firmware auto modes can leave internal gain changed after switching back to manual,
/// so fix exposure manually before inspection for repeatable brightness.
/// </para>
/// </remarks>
public sealed class WebcamCamera : ICamera
{
    private const string AutoOff = "Off";
    private const string AutoContinuous = "Continuous";

    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(10);

    private readonly WebcamOptions _options;
    private readonly ArrayPool<byte> _pool;
    private readonly object _sync = new();

    private CameraState _state = CameraState.Closed;
    private DeviceThread? _device;
    private volatile TriggerMode _triggerMode = TriggerMode.Off;
    private volatile PixelFormat _pixelFormat = PixelFormat.Bgr8;
    private int _pendingTriggers;

    // Created and released on the device thread; used by any MTA thread under _controlsLock.
    private readonly object _controlsLock = new();
    private UvcControls? _controls;

    // Negotiated stream size, cached on the device thread so reads need not wait for a frame.
    private volatile int _width;
    private volatile int _height;

    // Owned by the device thread.
    private VideoCapture? _capture;
    private Mat? _image;
    private long _frameId;
    private long _failingSince;

    public WebcamCamera(CameraInfo info, WebcamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(info);

        Info = info;
        _options = options ?? new WebcamOptions();
        _pool = _options.BufferPool ?? ArrayPool<byte>.Shared;
    }

    public CameraInfo Info { get; }

    public CameraState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public double ExposureTimeUs
    {
        get => (double)GetParameter(ParameterNames.ExposureTime);
        set => SetParameter(ParameterNames.ExposureTime, value);
    }

    public double Gain
    {
        get => (double)GetParameter(ParameterNames.Gain);
        set => SetParameter(ParameterNames.Gain, value);
    }

    public TriggerMode TriggerMode
    {
        get => _triggerMode;
        set => SetParameter(ParameterNames.TriggerMode, value.ToString());
    }

    public event EventHandler<FrameReceivedEventArgs>? FrameReceived;

    public event EventHandler<CameraErrorEventArgs>? ErrorOccurred;

    public event EventHandler<CameraStateChangedEventArgs>? StateChanged;

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_state != CameraState.Closed)
            {
                throw new InvalidOperationException($"Camera is {_state}; it must be closed to open.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var device = new DeviceThread($"Webcam device ({Info.Model})", _options.CommandTimeout);
        try
        {
            // Opening takes ~1 s on DirectShow; the caller's thread is never blocked.
            await device.InvokeAsync(OpenOnDevice).ConfigureAwait(false);
        }
        catch
        {
            device.Dispose();
            throw;
        }

        device.PumpFailed += OnPumpFailed;
        lock (_sync)
        {
            _device = device;
        }

        SetState(CameraState.Open);
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (State == CameraState.Acquiring)
        {
            await StopAcquisitionAsync(cancellationToken).ConfigureAwait(false);
        }

        DeviceThread? device;
        lock (_sync)
        {
            device = _device;
            _device = null;
        }

        if (device is not null)
        {
            device.PumpFailed -= OnPumpFailed;
            Task release = device.InvokeAsync(ReleaseOnDevice);
            if (device.IsCurrentThread)
            {
                device.Dispose(); // releases after the current handler returns
            }
            else
            {
                await release.ConfigureAwait(false);
                await Task.Run(device.Dispose, CancellationToken.None).ConfigureAwait(false);
            }
        }

        SetState(CameraState.Closed);
    }

    public async Task StartAcquisitionAsync(CancellationToken cancellationToken = default)
    {
        DeviceThread device;
        lock (_sync)
        {
            if (_state != CameraState.Open || _device is null)
            {
                throw new InvalidOperationException($"Camera is {_state}; it must be open to start acquisition.");
            }

            device = _device;
            _state = CameraState.Acquiring;
        }

        Interlocked.Exchange(ref _pendingTriggers, 0);
        try
        {
            await RunOnDeviceAsync(device, () =>
            {
                _frameId = 0;
                _failingSince = 0;
                device.Pump = GrabOnce;
            }).ConfigureAwait(false);
        }
        catch
        {
            lock (_sync)
            {
                _state = CameraState.Open;
            }

            throw;
        }

        RaiseStateChanged(CameraState.Open, CameraState.Acquiring);
    }

    public async Task StopAcquisitionAsync(CancellationToken cancellationToken = default)
    {
        DeviceThread? device;
        lock (_sync)
        {
            if (_state != CameraState.Acquiring)
            {
                return;
            }

            device = _device;
        }

        if (device is not null)
        {
            await RunOnDeviceAsync(device, () => device.Pump = null).ConfigureAwait(false);
        }

        SetState(CameraState.Open);
    }

    public void ExecuteSoftwareTrigger()
    {
        lock (_sync)
        {
            if (_state != CameraState.Acquiring)
            {
                throw new InvalidOperationException("Software trigger requires an acquiring camera.");
            }

            if (_triggerMode != TriggerMode.Software)
            {
                throw new InvalidOperationException("Software trigger requires TriggerMode = Software.");
            }
        }

        Interlocked.Increment(ref _pendingTriggers);
    }

    public IReadOnlyList<CameraParameter> GetParameters()
    {
        DeviceThread? device;
        CameraState state;
        lock (_sync)
        {
            device = _device;
            state = _state;
        }

        var parameters = new List<CameraParameter>();
        if (device is null)
        {
            // Closed: the controls are unknown until the device is opened.
            parameters.Add(new CameraParameter(ParameterNames.ExposureTime, ParameterType.Float) { IsAvailable = false, Unit = "us" });
            parameters.Add(new CameraParameter(ParameterNames.Gain, ParameterType.Float) { IsAvailable = false });
        }
        else
        {
            parameters.AddRange(WithControls(device, DescribeControls));
        }

        bool open = device is not null;
        bool idle = state == CameraState.Open;
        parameters.AddRange(
        [
            new CameraParameter(ParameterNames.TriggerMode, ParameterType.Enumeration)
            {
                IsWritable = true, EnumEntries = [nameof(TriggerMode.Off), nameof(TriggerMode.Software)],
                Description = "Software is emulated: the stream keeps running and the next frame after a trigger is published.",
            },
            new CameraParameter(ParameterNames.TriggerSoftware, ParameterType.Command)
            {
                IsAvailable = state == CameraState.Acquiring && _triggerMode == TriggerMode.Software,
                IsReadable = false,
                IsWritable = true,
            },
            new CameraParameter(ParameterNames.AcquisitionFrameRate, ParameterType.Float)
            {
                IsAvailable = false, Unit = "Hz",
                Description = "DirectShow does not report the negotiated frame rate; see the pipeline statistics.",
            },
            new CameraParameter(ParameterNames.PixelFormat, ParameterType.Enumeration)
            {
                IsWritable = state != CameraState.Acquiring,
                EnumEntries = [nameof(PixelFormat.Bgr8), nameof(PixelFormat.Mono8)],
                Description = "Mono8 is converted on the host.",
            },
            new CameraParameter(ParameterNames.Width, ParameterType.Integer)
            {
                IsAvailable = open, IsWritable = idle, Unit = "px",
                Description = "Width and Height select the nearest mode the driver supports; read back the result.",
            },
            new CameraParameter(ParameterNames.Height, ParameterType.Integer) { IsAvailable = open, IsWritable = idle, Unit = "px" },
        ]);
        return parameters;
    }

    public object GetParameter(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        switch (name)
        {
            case ParameterNames.TriggerMode:
                return _triggerMode.ToString();
            case ParameterNames.PixelFormat:
                return _pixelFormat.ToString();
            case ParameterNames.AcquisitionFrameRate:
                throw new InvalidOperationException($"{name} is not reported by DirectShow.");
            case ParameterNames.TriggerSoftware:
                throw new InvalidOperationException($"{name} is a command; use ExecuteCommand.");
        }

        if (!IsDeviceParameter(name))
        {
            throw new KeyNotFoundException($"Unknown parameter '{name}'.");
        }

        DeviceThread device = RequireDevice(name);
        return name switch
        {
            ParameterNames.Width => (long)_width,
            ParameterNames.Height => (long)_height,
            _ => WithControls(device, () => GetControl(name)),
        };
    }

    public void SetParameter(string name, object value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);

        switch (name)
        {
            case ParameterNames.TriggerMode:
                TriggerMode mode = ParseEnum<TriggerMode>(name, value);
                if (mode == TriggerMode.Hardware)
                {
                    throw new ArgumentException("A webcam has no hardware trigger input.", nameof(value));
                }

                _triggerMode = mode;
                Interlocked.Exchange(ref _pendingTriggers, 0);
                return;

            case ParameterNames.PixelFormat:
                PixelFormat format = ParseEnum<PixelFormat>(name, value);
                lock (_sync)
                {
                    if (_state == CameraState.Acquiring)
                    {
                        throw new InvalidOperationException($"{name} cannot be changed while acquiring.");
                    }

                    _pixelFormat = format;
                }

                return;

            case ParameterNames.AcquisitionFrameRate:
                throw new InvalidOperationException($"{name} is not supported by DirectShow capture.");

            case ParameterNames.TriggerSoftware:
                throw new InvalidOperationException($"{name} is a command; use ExecuteCommand.");
        }

        if (!IsDeviceParameter(name))
        {
            throw new KeyNotFoundException($"Unknown parameter '{name}'.");
        }

        DeviceThread device = RequireDevice(name);
        if (name is ParameterNames.Width or ParameterNames.Height)
        {
            if (State != CameraState.Open)
            {
                throw new InvalidOperationException($"{name} can only be changed while open and not acquiring.");
            }

            device.Invoke(() => SetResolution(name, value)); // renegotiates the stream: device thread only
            return;
        }

        WithControls(device, () =>
        {
            SetControl(name, value);
            return true;
        });
    }

    public void ExecuteCommand(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name != ParameterNames.TriggerSoftware)
        {
            throw new KeyNotFoundException($"Unknown command '{name}'.");
        }

        ExecuteSoftwareTrigger();
    }

    public async ValueTask DisposeAsync()
    {
        if (State != CameraState.Closed)
        {
            await CloseAsync().ConfigureAwait(false);
        }
    }

    // ---- Device thread -------------------------------------------------------------------

    private void OpenOnDevice()
    {
        (DirectShowDevice device, object filter) = DirectShowDevices.Bind(Info.DeviceId)
            ?? throw new InvalidOperationException($"Webcam '{Info.Model}' is not connected.");

        var controls = new UvcControls(filter);
        var capture = new VideoCapture(device.Index, VideoCaptureAPIs.DSHOW);
        if (!capture.IsOpened())
        {
            capture.Dispose();
            controls.Dispose();
            throw new InvalidOperationException(
                $"Webcam '{Info.Model}' could not be opened. It may be in use by another application; " +
                "if it stopped responding after a crash, reconnect it.");
        }

        _capture = capture;
        _image = new Mat();
        CacheSize();
        lock (_controlsLock)
        {
            _controls = controls;
        }
    }

    private void ReleaseOnDevice()
    {
        lock (_controlsLock)
        {
            _controls?.Dispose();
            _controls = null;
        }

        _image?.Dispose();
        _capture?.Dispose();
        _image = null;
        _capture = null;
    }

    private void CacheSize()
    {
        _width = (int)_capture!.Get(VideoCaptureProperties.FrameWidth);
        _height = (int)_capture.Get(VideoCaptureProperties.FrameHeight);
    }

    /// <summary>Pump body: reads one frame and publishes it unless it is gated by the software trigger.</summary>
    private void GrabOnce()
    {
        VideoCapture capture = _capture!;
        Mat image = _image!;

        // Only triggers issued before this read started may claim its frame.
        int pendingBefore = Volatile.Read(ref _pendingTriggers);
        long readStart = Stopwatch.GetTimestamp();
        if (!capture.Read(image) || image.Empty())
        {
            OnReadFailed();
            return;
        }

        // A slow "successful" read is how an unplugged device shows up (stale buffer returned
        // after the backend's wait timeout); confirm the device before publishing anything.
        if (Stopwatch.GetElapsedTime(readStart) >= _options.SlowReadThreshold && !DirectShowDevices.IsPresent(Info.DeviceId))
        {
            OnDisconnected($"Webcam '{Info.Model}' was removed.");
            return;
        }

        _failingSince = 0;
        if (_triggerMode == TriggerMode.Software)
        {
            if (pendingBefore == 0)
            {
                return; // triggers arriving during this read wait for the next frame
            }

            if (Interlocked.Decrement(ref _pendingTriggers) < 0)
            {
                // A mode change reset the count while this frame was being read.
                Interlocked.Increment(ref _pendingTriggers);
                return;
            }
        }

        Emit(image);
    }

    private void OnReadFailed()
    {
        long now = Stopwatch.GetTimestamp();
        if (_failingSince == 0)
        {
            _failingSince = now;
        }
        else if (Stopwatch.GetElapsedTime(_failingSince, now) >= _options.DisconnectTimeout)
        {
            OnDisconnected(
                $"Webcam '{Info.Model}' stopped delivering frames for {_options.DisconnectTimeout.TotalSeconds:0.#} s; it may have been unplugged.");
            return;
        }

        Thread.Sleep(ReadRetryDelay); // Read can fail immediately; do not spin.
    }

    private void OnDisconnected(string message)
    {
        _device!.Pump = null;
        RaiseError(CameraErrorKind.Disconnected, message + " Close and reopen the camera after reconnecting it.");
        SetState(CameraState.Faulted);
    }

    private void Emit(Mat image)
    {
        PixelFormat format = _pixelFormat;
        Frame frame = Frame.Rent(image.Width, image.Height, format, ++_frameId, DateTime.UtcNow, pool: _pool);
        try
        {
            WriteInto(image, frame);
            FrameReceived?.Invoke(this, new FrameReceivedEventArgs(frame));
        }
#pragma warning disable CA1031 // A failing subscriber must not stop acquisition.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            RaiseError(CameraErrorKind.FrameError, $"Frame #{frame.FrameId} handler failed: {ex.Message}", ex);
        }
        finally
        {
            frame.Dispose();
        }
    }

    /// <summary>Copies or converts the captured image straight into the frame buffer.</summary>
    private static unsafe void WriteInto(Mat source, Frame frame)
    {
        bool mono = frame.PixelFormat == PixelFormat.Mono8;
        MatType targetType = mono ? MatType.CV_8UC1 : MatType.CV_8UC3;
        MatType sourceType = source.Type();

        fixed (byte* data = frame.GetWritableData())
        {
            using Mat target = Mat.FromPixelData(frame.Height, frame.Width, targetType, (IntPtr)data, frame.Stride);
            if (sourceType == targetType)
            {
                source.CopyTo(target);
            }
            else if (sourceType == MatType.CV_8UC3)
            {
                Cv2.CvtColor(source, target, ColorConversionCodes.BGR2GRAY);
            }
            else if (sourceType == MatType.CV_8UC1)
            {
                Cv2.CvtColor(source, target, ColorConversionCodes.GRAY2BGR);
            }
            else
            {
                throw new NotSupportedException($"Captured image type {sourceType} is not supported.");
            }
        }
    }

    private void OnPumpFailed(Exception exception)
    {
        RaiseError(CameraErrorKind.Other, $"Acquisition failed: {exception.Message}", exception);
        SetState(CameraState.Faulted);
    }

    private List<CameraParameter> DescribeControls()
    {
        UvcControls controls = _controls!;
        var parameters = new List<CameraParameter>();

        UvcControl? exposure = controls.Find(UvcControls.Exposure);
        bool exposureAuto = exposure is not null && controls.Get(exposure).IsAuto;
        parameters.Add(new CameraParameter(ParameterNames.ExposureTime, ParameterType.Float)
        {
            IsAvailable = exposure is { SupportsManual: true },
            IsWritable = exposure is not null && !exposureAuto,
            Minimum = exposure is null ? null : ExposureMapping.ToMicroseconds(exposure.Minimum),
            Maximum = exposure is null ? null : ExposureMapping.ToMicroseconds(exposure.Maximum),
            Unit = "us",
            Description = "Quantized to powers of two (DirectShow log2 seconds). Writable while ExposureAuto is Off; " +
                          "fix it manually for repeatable inspection.",
        });
        if (exposure is { SupportsAuto: true })
        {
            parameters.Add(DescribeAuto(exposure));
        }

        UvcControl? gain = controls.Find(UvcControls.Gain);
        parameters.Add(new CameraParameter(ParameterNames.Gain, ParameterType.Float)
        {
            IsAvailable = gain is { SupportsManual: true },
            IsWritable = gain is not null && !(gain.SupportsAuto && controls.Get(gain).IsAuto),
            Minimum = gain?.Minimum,
            Maximum = gain?.Maximum,
            Increment = gain?.Step,
            Unit = "raw",
            Description = "Device-specific units, not dB.",
        });
        if (gain is { SupportsAuto: true })
        {
            parameters.Add(DescribeAuto(gain));
        }

        foreach (UvcControl control in controls.Available)
        {
            if (control.Name is UvcControls.Exposure or UvcControls.Gain)
            {
                continue;
            }

            parameters.Add(new CameraParameter(control.Name, ParameterType.Integer)
            {
                IsAvailable = control.SupportsManual,
                IsWritable = !(control.SupportsAuto && controls.Get(control).IsAuto),
                Minimum = control.Minimum,
                Maximum = control.Maximum,
                Increment = control.Step,
                Unit = control.Spec.Unit,
                Description = string.Create(CultureInfo.InvariantCulture, $"UVC control, default {control.Default}."),
            });
            if (control.SupportsAuto)
            {
                parameters.Add(DescribeAuto(control));
            }
        }

        return parameters;
    }

    private static CameraParameter DescribeAuto(UvcControl control) =>
        new(control.Spec.AutoName, ParameterType.Enumeration) { IsWritable = true, EnumEntries = [AutoOff, AutoContinuous] };

    /// <summary>Reads a UVC control parameter; caller holds <see cref="_controlsLock"/>.</summary>
    private object GetControl(string name)
    {
        switch (name)
        {
            case ParameterNames.ExposureTime:
                return ExposureMapping.ToMicroseconds(_controls!.Get(RequireControl(UvcControls.Exposure, name)).Value);
            case ParameterNames.Gain:
                return (double)_controls!.Get(RequireControl(UvcControls.Gain, name)).Value;
        }

        (UvcControl control, bool isAuto) = ResolveControl(name);
        (int value, bool auto) = _controls!.Get(control);
        return isAuto ? (auto ? AutoContinuous : AutoOff) : (long)value;
    }

    /// <summary>Writes a UVC control parameter; caller holds <see cref="_controlsLock"/>.</summary>
    private void SetControl(string name, object value)
    {
        UvcControls controls = _controls!;
        switch (name)
        {
            case ParameterNames.ExposureTime:
            {
                UvcControl exposure = RequireManual(RequireControl(UvcControls.Exposure, name), name);
                double microseconds = InRange(name, ToDouble(value),
                    ExposureMapping.ToMicroseconds(exposure.Minimum), ExposureMapping.ToMicroseconds(exposure.Maximum));
                controls.Set(exposure, Math.Clamp(ExposureMapping.ToDeviceValue(microseconds), exposure.Minimum, exposure.Maximum), auto: false);
                return;
            }

            case ParameterNames.Gain:
            {
                UvcControl gain = RequireManual(RequireControl(UvcControls.Gain, name), name);
                controls.Set(gain, (int)Math.Round(InRange(name, ToDouble(value), gain.Minimum, gain.Maximum)), auto: false);
                return;
            }
        }

        (UvcControl control, bool isAuto) = ResolveControl(name);
        if (isAuto)
        {
            bool auto = ParseAuto(name, value);
            controls.Set(control, controls.Get(control).Value, auto);
        }
        else
        {
            RequireManual(control, name);
            controls.Set(control, (int)InRange(name, ToDouble(value), control.Minimum, control.Maximum), auto: false);
        }
    }

    private void SetResolution(string name, object value)
    {
        VideoCapture capture = _capture!;
        long requested = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requested, name);

        double width = name == ParameterNames.Width ? requested : capture.Get(VideoCaptureProperties.FrameWidth);
        double height = name == ParameterNames.Height ? requested : capture.Get(VideoCaptureProperties.FrameHeight);

        // DirectShow renegotiates the media type on each call; set both so the pair stays consistent.
        bool accepted = capture.Set(VideoCaptureProperties.FrameWidth, width) && capture.Set(VideoCaptureProperties.FrameHeight, height);
        CacheSize();
        if (!accepted)
        {
            throw new ArgumentException($"The webcam rejected {width}x{height}.", nameof(value));
        }
    }

    /// <summary>
    /// Runs a UVC control access under the controls lock on the calling (MTA) thread, or on the
    /// device thread when called from an STA thread.
    /// </summary>
    private T WithControls<T>(DeviceThread device, Func<T> access)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA && !device.IsCurrentThread)
        {
            return device.Invoke(() => WithControls(device, access));
        }

        lock (_controlsLock)
        {
            if (_controls is null)
            {
                throw new InvalidOperationException("The camera is closed.");
            }

            return access();
        }
    }

    private UvcControl RequireControl(string controlName, string parameterName) =>
        _controls!.Find(controlName) is { SupportsManual: true } control
            ? control
            : throw new InvalidOperationException($"{parameterName} is not supported by this webcam.");

    private UvcControl RequireManual(UvcControl control, string parameterName) =>
        control.SupportsAuto && _controls!.Get(control).IsAuto
            ? throw new InvalidOperationException($"{parameterName} is controlled automatically; set {control.Spec.AutoName} to Off first.")
            : control;

    /// <summary>Maps a generic UVC parameter name to its control; IsAuto is true for the auto switch.</summary>
    private (UvcControl Control, bool IsAuto) ResolveControl(string name)
    {
        foreach (UvcControlSpec spec in UvcControls.AllSpecs)
        {
            bool isValue = spec.Name == name && spec.Name is not (UvcControls.Exposure or UvcControls.Gain);
            bool isAuto = spec.AutoName == name;
            if (!isValue && !isAuto)
            {
                continue;
            }

            UvcControl? control = _controls!.Find(spec.Name);
            if (control is null || (isAuto && !control.SupportsAuto))
            {
                throw new InvalidOperationException($"{name} is not supported by this webcam.");
            }

            return (control, isAuto);
        }

        throw new KeyNotFoundException($"Unknown parameter '{name}'.");
    }

    // ---- Helpers -------------------------------------------------------------------------

    private static bool IsDeviceParameter(string name) =>
        name is ParameterNames.ExposureTime or ParameterNames.Gain or ParameterNames.Width or ParameterNames.Height
        || UvcControls.AllSpecs.Any(s => (s.Name == name && s.Name is not (UvcControls.Exposure or UvcControls.Gain)) || s.AutoName == name);

    private DeviceThread RequireDevice(string name)
    {
        lock (_sync)
        {
            return _device ?? throw new InvalidOperationException($"{name} is available after the camera is opened.");
        }
    }

    private static Task RunOnDeviceAsync(DeviceThread device, Action work)
    {
        if (device.IsCurrentThread)
        {
            work();
            return Task.CompletedTask;
        }

        return device.InvokeAsync(work);
    }

    private void SetState(CameraState newState)
    {
        CameraState old;
        lock (_sync)
        {
            old = _state;
            if (old == newState)
            {
                return;
            }

            _state = newState;
        }

        RaiseStateChanged(old, newState);
    }

    private void RaiseStateChanged(CameraState oldState, CameraState newState) =>
        StateChanged?.Invoke(this, new CameraStateChangedEventArgs(oldState, newState));

    private void RaiseError(CameraErrorKind kind, string message, Exception? exception = null) =>
        ErrorOccurred?.Invoke(this, new CameraErrorEventArgs(kind, message, exception));

    private static double ToDouble(object value) => Convert.ToDouble(value, CultureInfo.InvariantCulture);

    private static double InRange(string name, double value, double min, double max) =>
        value >= min && value <= max
            ? value
            : throw new ArgumentOutOfRangeException(name, value, $"{name} must be between {min} and {max}.");

    private static bool ParseAuto(string name, object value)
    {
        string? text = Convert.ToString(value, CultureInfo.InvariantCulture);
        return string.Equals(text, AutoContinuous, StringComparison.OrdinalIgnoreCase) ? true
            : string.Equals(text, AutoOff, StringComparison.OrdinalIgnoreCase) ? false
            : throw new ArgumentException($"'{text}' is not a valid {name}; use {AutoOff} or {AutoContinuous}.", nameof(value));
    }

    private static TEnum ParseEnum<TEnum>(string name, object value)
        where TEnum : struct, Enum
    {
        if (value is TEnum typed)
        {
            return typed;
        }

        string? text = Convert.ToString(value, CultureInfo.InvariantCulture);
        return Enum.TryParse(text, ignoreCase: true, out TEnum parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new ArgumentException($"'{text}' is not a valid {name}.", nameof(value));
    }
}
