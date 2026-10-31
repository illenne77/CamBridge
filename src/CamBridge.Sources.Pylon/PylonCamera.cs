#if HAS_PYLON
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using Basler.Pylon;
using CamBridge.Core;
using OpenCvSharp;
using CoreCameraInfo = CamBridge.Core.CameraInfo;
using ICamera = CamBridge.Core.ICamera;
using CorePixelFormat = CamBridge.Core.PixelFormat;
using CoreTriggerMode = CamBridge.Core.TriggerMode;
using Frame = CamBridge.Core.Frame;
using PylonCameraApi = Basler.Pylon.Camera;

namespace CamBridge.Sources.Pylon;

/// <summary>
/// A Basler camera through the pylon .NET API. No Basler type leaves this class.
/// </summary>
/// <remarks>
/// <para>
/// pylon's Camera is internally synchronized, so parameters are accessed on the caller's thread.
/// Frames arrive on pylon's grab loop thread (OneByOne strategy); the handler only copies the
/// buffer into a pooled <see cref="Frame"/> and raises <see cref="FrameReceived"/>.
/// </para>
/// <para>
/// <see cref="Frame.FrameId"/> is the unwrapped block ID, so frames lost in transport become
/// gaps the pipeline counts as camera-side drops. Vendor-neutral names (ExposureTime, Gain,
/// AcquisitionFrameRate) resolve to SFNC 2.x nodes or their SFNC 1.x equivalents.
/// </para>
/// </remarks>
internal sealed class PylonCamera(CoreCameraInfo info, PylonOptions options) : ICamera
{
    private const string PacketSizeNode = "GevSCPSPacketSize";

    private static readonly HashSet<string> OwnNames = new(StringComparer.Ordinal)
    {
        ParameterNames.TriggerMode, ParameterNames.TriggerSoftware,
    };

    /// <summary>
    /// Transport counters of the stream grabber. They live outside the device node map, so a plain
    /// name does not reach them; exposed read-only under these names to judge a GigE link.
    /// </summary>
    private static readonly Dictionary<string, IntegerName> StreamStatistics = new(StringComparer.Ordinal)
    {
        ["Statistic_Total_Buffer_Count"] = PLStream.Statistic_Total_Buffer_Count,
        ["Statistic_Failed_Buffer_Count"] = PLStream.Statistic_Failed_Buffer_Count,
        ["Statistic_Buffer_Underrun_Count"] = PLStream.Statistic_Buffer_Underrun_Count,
        ["Statistic_Total_Packet_Count"] = PLStream.Statistic_Total_Packet_Count,
        ["Statistic_Failed_Packet_Count"] = PLStream.Statistic_Failed_Packet_Count,
        ["Statistic_Resend_Request_Count"] = PLStream.Statistic_Resend_Request_Count,
        ["Statistic_Resend_Packet_Count"] = PLStream.Statistic_Resend_Packet_Count,
        ["Statistic_Missed_Frame_Count"] = PLStream.Statistic_Missed_Frame_Count,
        ["Statistic_Resynchronization_Count"] = PLStream.Statistic_Resynchronization_Count,
    };

    private readonly ArrayPool<byte> _pool = options.BufferPool ?? ArrayPool<byte>.Shared;
    private readonly object _sync = new();

    private CameraState _state = CameraState.Closed;
    private PylonCameraApi? _camera;
    private volatile CoreTriggerMode _triggerMode = CoreTriggerMode.Off;
    private BlockIdUnwrapper _ids = new(sixteenBitWrap: false);
    private bool _isGigE;
    private PixelType _reportedUnsupported;

    public CoreCameraInfo Info { get; } = info ?? throw new ArgumentNullException(nameof(info));

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
        get => Convert.ToDouble(GetParameter(ParameterNames.ExposureTime), CultureInfo.InvariantCulture);
        set => SetParameter(ParameterNames.ExposureTime, value);
    }

    public double Gain
    {
        get => Convert.ToDouble(GetParameter(ParameterNames.Gain), CultureInfo.InvariantCulture);
        set => SetParameter(ParameterNames.Gain, value);
    }

    public CoreTriggerMode TriggerMode
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

        PylonCameraApi camera = await Task.Run(() => OpenDevice(cancellationToken), cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _camera = camera;
        }

        SetState(CameraState.Open);
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (State == CameraState.Acquiring)
        {
            await StopAcquisitionAsync(cancellationToken).ConfigureAwait(false);
        }

        PylonCameraApi? camera;
        lock (_sync)
        {
            camera = _camera;
            _camera = null;
        }

        if (camera is not null)
        {
            await Task.Run(() => CloseDevice(camera), CancellationToken.None).ConfigureAwait(false);
        }

        SetState(CameraState.Closed);
    }

    public async Task StartAcquisitionAsync(CancellationToken cancellationToken = default)
    {
        PylonCameraApi camera;
        lock (_sync)
        {
            if (_state != CameraState.Open || _camera is null)
            {
                throw new InvalidOperationException($"Camera is {_state}; it must be open to start acquisition.");
            }

            camera = _camera;
            _state = CameraState.Acquiring;
        }

        _ids = new BlockIdUnwrapper(_isGigE);
        try
        {
            await Task.Run(() => Grabber(camera).Start(GrabStrategy.OneByOne, GrabLoop.ProvidedByStreamGrabber), cancellationToken).ConfigureAwait(false);
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
        PylonCameraApi? camera;
        lock (_sync)
        {
            if (_state != CameraState.Acquiring)
            {
                return;
            }

            camera = _camera;
        }

        if (camera is not null)
        {
            // Stop waits for the grab loop thread; never call it on that thread or the UI thread.
            await Task.Run(Grabber(camera).Stop, CancellationToken.None).ConfigureAwait(false);
        }

        SetState(CameraState.Open);
    }

    public void ExecuteSoftwareTrigger()
    {
        PylonCameraApi camera;
        lock (_sync)
        {
            if (_state != CameraState.Acquiring || _camera is null)
            {
                throw new InvalidOperationException("Software trigger requires an acquiring camera.");
            }

            if (_triggerMode != CoreTriggerMode.Software)
            {
                throw new InvalidOperationException("Software trigger requires TriggerMode = Software.");
            }

            camera = _camera;
        }

        // A trigger sent while the sensor is still busy would be ignored by the camera.
        if (camera.CanWaitForFrameTriggerReady && !camera.WaitForFrameTriggerReady(1000, TimeoutHandling.Return))
        {
            throw new InvalidOperationException("The camera is not ready for the next frame trigger.");
        }

        camera.ExecuteSoftwareTrigger();
    }

    public IReadOnlyList<CameraParameter> GetParameters()
    {
        PylonCameraApi? camera;
        CameraState state;
        lock (_sync)
        {
            camera = _camera;
            state = _state;
        }

        var parameters = new List<CameraParameter>();
        var listed = new HashSet<string>(StringComparer.Ordinal);
        void Add(CameraParameter p)
        {
            if (listed.Add(p.Name))
            {
                parameters.Add(p);
            }
        }

        if (camera is null)
        {
            Add(new CameraParameter(ParameterNames.ExposureTime, ParameterType.Float) { IsAvailable = false, Unit = "us" });
            Add(new CameraParameter(ParameterNames.Gain, ParameterType.Float) { IsAvailable = false });
        }
        else
        {
            // Vendor-neutral names first, described from whichever node implements them.
            foreach (string name in FeatureNames.AliasedNames)
            {
                string? node = Resolve(camera, name);
                Add(node is null
                    ? new CameraParameter(name, ParameterType.Float) { IsAvailable = false }
                    : Describe(camera.Parameters[node], name) with { Type = ParameterType.Float });
            }
        }

        Add(new CameraParameter(ParameterNames.TriggerMode, ParameterType.Enumeration)
        {
            IsWritable = camera is not null,
            EnumEntries = [nameof(CoreTriggerMode.Off), nameof(CoreTriggerMode.Software), nameof(CoreTriggerMode.Hardware)],
            Description = "Maps to TriggerSelector=FrameStart with TriggerMode/TriggerSource. Choose the input line with TriggerSource.",
        });
        Add(new CameraParameter(ParameterNames.TriggerSoftware, ParameterType.Command)
        {
            IsAvailable = state == CameraState.Acquiring && _triggerMode == CoreTriggerMode.Software,
            IsReadable = false,
            IsWritable = true,
        });

        if (camera is not null)
        {
            foreach (IParameter node in camera.Parameters)
            {
                // The collection also yields camera-instance and stream nodes (e.g. MaxNumBuffer) that a
                // plain name does not reach; list only device nodes so every listed name works with GetParameter.
                if (!OwnNames.Contains(node.Name) && IsListed(node) && !camera.Parameters[node.Name].IsEmpty)
                {
                    Add(Describe(node, node.Name));
                }
            }

            foreach ((string name, IntegerName node) in StreamStatistics)
            {
                IIntegerParameter statistic = camera.Parameters[node];
                if (!statistic.IsEmpty)
                {
                    Add(Describe(statistic, name));
                }
            }
        }

        return parameters;
    }

    public object GetParameter(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name == ParameterNames.TriggerMode)
        {
            return _triggerMode.ToString();
        }

        if (name == ParameterNames.TriggerSoftware)
        {
            throw new InvalidOperationException($"{name} is a command; use ExecuteCommand.");
        }

        PylonCameraApi camera = RequireCamera(name);
        IParameter node = Node(camera, name);
        if (!node.IsReadable)
        {
            throw new InvalidOperationException($"{name} is not readable in the camera's current state.");
        }

        object value = node switch
        {
            IFloatParameter f => f.GetValue(),
            IIntegerParameter i => i.GetValue(),
            IEnumParameter e => e.GetValue(),
            IBooleanParameter b => b.GetValue(),
            IStringParameter s => s.GetValue(),
            ICommandParameter => throw new InvalidOperationException($"{name} is a command; use ExecuteCommand."),
            _ => throw new NotSupportedException($"{name} has an unsupported node type."),
        };

        // Vendor-neutral names are Float even when an SFNC 1.x integer node (GainRaw) implements them.
        return FeatureNames.AliasedNames.Contains(name) && value is long raw ? (double)raw : value;
    }

    public void SetParameter(string name, object value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);

        PylonCameraApi camera = RequireCamera(name);
        if (name == ParameterNames.TriggerMode)
        {
            SetTriggerMode(camera, ParseEnum<CoreTriggerMode>(name, value));
            return;
        }

        if (name == ParameterNames.TriggerSoftware)
        {
            throw new InvalidOperationException($"{name} is a command; use ExecuteCommand.");
        }

        if (name == ParameterNames.AcquisitionFrameRate)
        {
            // The rate applies only when enabled, and some cameras lock the value until then: enable first.
            camera.Parameters[PLCamera.AcquisitionFrameRateEnable].TrySetValue(true);
        }

        IParameter node = Node(camera, name);
        if (!node.IsWritable)
        {
            throw new InvalidOperationException(
                $"{name} is not writable in the camera's current state (an auto function may be on, or acquisition is running).");
        }

        switch (node)
        {
            case IFloatParameter f:
                f.SetValue(InRange(name, ToDouble(value), f.GetMinimum(), f.GetMaximum()));
                break;

            case IIntegerParameter i:
                double requested = InRange(name, ToDouble(value), i.GetMinimum(), i.GetMaximum());
                i.SetValue((long)Math.Round(requested), IntegerValueCorrection.Nearest);
                break;

            case IEnumParameter e:
                string entry = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                if (!e.CanSetValue(entry))
                {
                    throw new ArgumentException($"'{entry}' is not a valid {name}; use one of {string.Join(", ", e)}.", nameof(value));
                }

                e.SetValue(entry);
                break;

            case IBooleanParameter b:
                b.SetValue(value is bool flag ? flag : bool.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!));
                break;

            case IStringParameter s:
                s.SetValue(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                break;

            default:
                throw new InvalidOperationException($"{name} cannot be set (node type {node.GetType().Name}).");
        }

        if (name == PacketSizeNode)
        {
            // Negotiation at the next start would replace a size chosen by hand.
            camera.Parameters[PLStream.AutoPacketSize].TrySetValue(false);
        }
    }

    public void ExecuteCommand(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name == ParameterNames.TriggerSoftware)
        {
            ExecuteSoftwareTrigger();
            return;
        }

        if (Node(RequireCamera(name), name) is not ICommandParameter command)
        {
            throw new KeyNotFoundException($"Unknown command '{name}'.");
        }

        command.Execute();
    }

    public async ValueTask DisposeAsync()
    {
        if (State != CameraState.Closed)
        {
            await CloseAsync().ConfigureAwait(false);
        }
    }

    // ---- Device ----------------------------------------------------------------------

    private PylonCameraApi OpenDevice(CancellationToken cancellationToken)
    {
        ICameraInfo device = CameraFinder.Enumerate().FirstOrDefault(d => d.ContainsKey(CameraInfoKey.FullName) && d[CameraInfoKey.FullName] == Info.DeviceId)
            ?? throw new InvalidOperationException($"Camera '{Info.DisplayName}' is not connected.");
        cancellationToken.ThrowIfCancellationRequested();

        var camera = new PylonCameraApi(device);
        try
        {
            camera.ConnectionLost += OnConnectionLost;
            Grabber(camera).ImageGrabbed += OnImageGrabbed;
            camera.Open();

            _isGigE = PylonProvider.InterfaceOf(device[CameraInfoKey.DeviceType] ?? string.Empty) == CameraInterface.GigE;
            camera.Parameters[PLCamera.AcquisitionMode].TrySetValue(PLCamera.AcquisitionMode.Continuous); // default may be SingleFrame
            camera.Parameters[PLCameraInstance.MaxNumBuffer].TrySetValue(options.BufferCount);

            // A format left over from another tool (e.g. Mono12) would only produce errors here.
            IEnumParameter pixelFormat = camera.Parameters[PLCamera.PixelFormat];
            if (!IsSupportedPixelFormat(pixelFormat.GetValueOrDefault(string.Empty)))
            {
                pixelFormat.TrySetValue(PLCamera.PixelFormat.Mono8);
            }

            if (_isGigE)
            {
                camera.Parameters[PLStream.AutoPacketSize].TrySetValue(options.AutoPacketSize);
            }

            if (_isGigE && options.HeartbeatTimeout is TimeSpan heartbeat && !Debugger.IsAttached)
            {
                camera.Parameters[PLTransportLayer.HeartbeatTimeout].TrySetValue((int)heartbeat.TotalMilliseconds, IntegerValueCorrection.Nearest);
            }

            _triggerMode = ReadTriggerMode(camera);
            return camera;
        }
        catch
        {
            CloseDevice(camera);
            throw;
        }
    }

    private void CloseDevice(PylonCameraApi camera)
    {
        camera.ConnectionLost -= OnConnectionLost;
        if (camera.StreamGrabber is { } grabber)
        {
            grabber.ImageGrabbed -= OnImageGrabbed;
        }
        try
        {
            if (camera.IsOpen)
            {
                camera.Close();
            }
        }
        finally
        {
            camera.Dispose();
        }
    }

    private static CoreTriggerMode ReadTriggerMode(PylonCameraApi camera)
    {
        camera.Parameters[PLCamera.TriggerSelector].TrySetValue(PLCamera.TriggerSelector.FrameStart);
        if (camera.Parameters[PLCamera.TriggerMode].GetValueOrDefault(PLCamera.TriggerMode.Off) == PLCamera.TriggerMode.Off)
        {
            return CoreTriggerMode.Off;
        }

        return camera.Parameters[PLCamera.TriggerSource].GetValueOrDefault(string.Empty) == PLCamera.TriggerSource.Software
            ? CoreTriggerMode.Software
            : CoreTriggerMode.Hardware;
    }

    private void SetTriggerMode(PylonCameraApi camera, CoreTriggerMode mode)
    {
        camera.Parameters[PLCamera.TriggerSelector].TrySetValue(PLCamera.TriggerSelector.FrameStart);
        if (mode == CoreTriggerMode.Off)
        {
            camera.Parameters[PLCamera.TriggerMode].SetValue(PLCamera.TriggerMode.Off);
        }
        else
        {
            IEnumParameter source = camera.Parameters[PLCamera.TriggerSource];
            if (mode == CoreTriggerMode.Software)
            {
                source.SetValue(PLCamera.TriggerSource.Software);
            }
            else if (!source.GetValue().StartsWith("Line", StringComparison.Ordinal))
            {
                source.SetValue(PLCamera.TriggerSource.Line1); // keep a line the user chose; otherwise default to Line1
            }

            camera.Parameters[PLCamera.TriggerMode].SetValue(PLCamera.TriggerMode.On);
        }

        _triggerMode = mode;
    }

    private void OnImageGrabbed(object? sender, ImageGrabbedEventArgs e)
    {
        // Runs on pylon's grab loop thread. Exceptions would stop pylon's event notification.
        try
        {
            IGrabResult result = e.GrabResult;
            if (!result.GrabSucceeded)
            {
                RaiseError(CameraErrorKind.FrameError, string.Create(CultureInfo.InvariantCulture,
                    $"Grab failed (0x{result.ErrorCode:X}): {result.ErrorDescription}"));
                return; // the missing block ID is counted as a camera-side drop
            }

            long frameId = _ids.Next(result.BlockID);
            using IDataContainer? container = result.Container;
            if (container is null)
            {
                return;
            }

            using IDataComponent? component = container.GetFirstImageDataComponent();
            if (component is null)
            {
                return; // e.g. a chunk-only result
            }

            Frame? frame = CopyFrame(component, frameId, result.Timestamp);
            if (frame is null)
            {
                return;
            }

            try
            {
                FrameReceived?.Invoke(this, new FrameReceivedEventArgs(frame));
            }
            finally
            {
                frame.Dispose();
            }
        }
#pragma warning disable CA1031 // A failing subscriber or conversion must not stop acquisition.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            RaiseError(CameraErrorKind.FrameError, $"Frame handling failed: {ex.Message}", ex);
        }
    }

    /// <summary>Copies (or converts) the grab buffer into a pooled frame; null for unsupported formats.</summary>
    private unsafe Frame? CopyFrame(IDataComponent component, long frameId, long deviceTimestamp)
    {
        int width = component.Width;
        int height = component.Height;
        PixelType type = component.PixelTypeValue;
        ReadOnlySpan<byte> source = component.AsReadOnlySpan();

        (CorePixelFormat format, int bytesPerPixel, ColorConversionCodes? conversion) = type switch
        {
            PixelType.Mono8 => (CorePixelFormat.Mono8, 1, (ColorConversionCodes?)null),
            PixelType.BGR8packed => (CorePixelFormat.Bgr8, 3, null),
            PixelType.RGB8packed => (CorePixelFormat.Bgr8, 3, ColorConversionCodes.RGB2BGR),
            // GenICam names the first row's first two pixels; OpenCV names the second row's, hence the swap.
            PixelType.BayerRG8 => (CorePixelFormat.Bgr8, 1, ColorConversionCodes.BayerBG2BGR),
            PixelType.BayerBG8 => (CorePixelFormat.Bgr8, 1, ColorConversionCodes.BayerRG2BGR),
            PixelType.BayerGR8 => (CorePixelFormat.Bgr8, 1, ColorConversionCodes.BayerGB2BGR),
            PixelType.BayerGB8 => (CorePixelFormat.Bgr8, 1, ColorConversionCodes.BayerGR2BGR),
            _ => (default, 0, null),
        };

        if (bytesPerPixel == 0)
        {
            if (_reportedUnsupported != type)
            {
                _reportedUnsupported = type; // report once per format, not per frame
                RaiseError(CameraErrorKind.FrameError, $"Pixel format {type} is not supported; use Mono8, BGR8, RGB8 or an 8-bit Bayer format.");
            }

            return null;
        }

        int sourceStride = (width * bytesPerPixel) + (int)component.PaddingX;
        Frame frame = Frame.Rent(width, height, format, frameId, DateTime.UtcNow, deviceTimestamp, pool: _pool);
        try
        {
            Span<byte> target = frame.GetWritableData();
            if (conversion is null)
            {
                int rowBytes = width * bytesPerPixel;
                for (int y = 0; y < height; y++)
                {
                    source.Slice(y * sourceStride, rowBytes).CopyTo(target.Slice(y * frame.Stride, rowBytes));
                }
            }
            else
            {
                fixed (byte* src = source)
                fixed (byte* dst = target)
                {
                    MatType sourceType = bytesPerPixel == 1 ? MatType.CV_8UC1 : MatType.CV_8UC3;
                    using Mat input = Mat.FromPixelData(height, width, sourceType, (IntPtr)src, sourceStride);
                    using Mat output = Mat.FromPixelData(height, width, MatType.CV_8UC3, (IntPtr)dst, frame.Stride);
                    Cv2.CvtColor(input, output, conversion.Value);
                }
            }

            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    private void OnConnectionLost(object? sender, EventArgs e)
    {
        // Raised inside pylon's camera lock: only change state and notify, never call into the camera.
        RaiseError(CameraErrorKind.Disconnected, $"Camera '{Info.DisplayName}' was disconnected. Close and reopen it after reconnecting.");
        SetState(CameraState.Faulted);
    }

    // ---- Parameters ------------------------------------------------------------------

    private static IStreamGrabber Grabber(PylonCameraApi camera) =>
        camera.StreamGrabber ?? throw new InvalidOperationException("pylon provided no stream grabber for this camera.");

    private static string? Resolve(PylonCameraApi camera, string name) =>
        FeatureNames.Resolve(name, n => !camera.Parameters[n].IsEmpty);

    private static IParameter Node(PylonCameraApi camera, string name)
    {
        if (StreamStatistics.TryGetValue(name, out IntegerName statistic) && camera.Parameters[statistic] is { IsEmpty: false } counter)
        {
            return counter;
        }

        return Resolve(camera, name) is string node
            ? camera.Parameters[node]
            : throw new KeyNotFoundException($"Unknown parameter '{name}'.");
    }

    private static bool IsSupportedPixelFormat(string pixelFormat) =>
        pixelFormat is "Mono8" or "BGR8" or "BGR8Packed" or "RGB8" or "RGB8Packed" or "BayerRG8" or "BayerBG8" or "BayerGR8" or "BayerGB8";

    private bool IsListed(IParameter node)
    {
        if (node.IsEmpty || node is not (IFloatParameter or IIntegerParameter or IEnumParameter or IBooleanParameter or IStringParameter or ICommandParameter))
        {
            return false;
        }

        string visibility = node.Advanced?.GetPropertyOrDefault(AdvancedParameterAccessKey.Visibility, string.Empty) ?? string.Empty;
        return Enum.TryParse(visibility, out NodeVisibility level) && level <= options.MaxVisibility;
    }

    private static CameraParameter Describe(IParameter node, string name)
    {
        string? Property(string key)
        {
            string? value = node.Advanced?.GetPropertyOrDefault(key, string.Empty);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        bool readable = node.IsReadable;
        ParameterType type = ParameterType.String;
        double? min = null;
        double? max = null;
        double? increment = null;
        IReadOnlyList<string> entries = [];
        switch (node)
        {
            case IFloatParameter f:
                type = ParameterType.Float;
                if (readable)
                {
                    (min, max) = (f.GetMinimum(), f.GetMaximum());
                }

                break;
            case IIntegerParameter i:
                type = ParameterType.Integer;
                if (readable)
                {
                    (min, max, increment) = (i.GetMinimum(), i.GetMaximum(), i.GetIncrement());
                }

                break;
            case IEnumParameter e:
                type = ParameterType.Enumeration;
                // Enumerating yields the settable values; GetAllValues also has entries this model lacks.
                // Nothing is settable while the node is locked (e.g. during acquisition), so show the current one.
                entries = !readable ? [] : node.IsWritable ? e.ToList() : [e.GetValue()];
                break;
            case IBooleanParameter:
                type = ParameterType.Boolean;
                break;
            case ICommandParameter:
                type = ParameterType.Command;
                break;
        }

        string? description = Property(AdvancedParameterAccessKey.ShortDescription) ?? Property(AdvancedParameterAccessKey.Description);
        if (name != node.Name)
        {
            description = $"Implemented by {node.Name}. {description}".Trim();
        }

        return new CameraParameter(name, type)
        {
            IsReadable = type != ParameterType.Command && readable,
            IsWritable = node.IsWritable,
            Minimum = min,
            Maximum = max,
            Increment = increment,
            Unit = FeatureNames.UnitOf(node.Name, Property(AdvancedParameterAccessKey.Unit)),
            EnumEntries = entries,
            Description = description,
        };
    }

    private PylonCameraApi RequireCamera(string name)
    {
        lock (_sync)
        {
            return _camera ?? throw new InvalidOperationException($"{name} is available after the camera is opened.");
        }
    }

    // ---- Helpers ---------------------------------------------------------------------

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
#endif
