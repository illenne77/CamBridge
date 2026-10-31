using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using CamBridge.Core;
using OpenCvSharp;
using PixelFormat = CamBridge.Core.PixelFormat;

namespace CamBridge.Sources.ImageFolder;

/// <summary>
/// Plays the images of a folder as if they came from a camera: free-run at a configurable
/// frame rate or one image per software trigger.
/// </summary>
/// <remarks>
/// <para>
/// All images are decoded at <see cref="OpenAsync"/> (bounded by
/// <see cref="ImageFolderOptions.MaxMemoryBytes"/>) and must share one size, like a real sensor.
/// Frames are emitted on a dedicated acquisition thread.
/// </para>
/// <para>
/// Exposure and gain are simulated digitally: the pixel values are scaled by
/// (ExposureTime / 10000 µs) x 10^(Gain / 20) while copying into the frame buffer, so the
/// parameter panel visibly affects inspection without real hardware.
/// </para>
/// </remarks>
public sealed class ImageFolderCamera : ICamera
{
    public const string LoopParameter = "Loop";

    private const double ReferenceExposureUs = 10_000;
    private const double MinExposureUs = 100;
    private const double MaxExposureUs = 1_000_000;
    private const double MaxGainDb = 24;
    private const double MinFrameRate = 0.1;
    private const double MaxFrameRate = 1_000;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly long SpinThresholdTicks = Stopwatch.Frequency / 500; // last 2 ms are spun, not slept

    private readonly ImageFolderOptions _options;
    private readonly ArrayPool<byte> _pool;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _triggers = new(0);

    private CameraState _state = CameraState.Closed;
    private DecodedImage[] _images = [];
    private int _width;
    private int _height;

    // Parameters. Written under _sync, read lock-free by the acquisition thread.
    private double _exposureUs = ReferenceExposureUs;
    private double _gainDb;
    private volatile TriggerMode _triggerMode = TriggerMode.Off;
    private double _frameRate = 30;
    private PixelFormat _pixelFormat = PixelFormat.Mono8;
    private volatile bool _loop = true;
    private volatile byte[]? _lut; // null = identity

    private Thread? _thread;
    private volatile bool _stopRequested;

    public ImageFolderCamera(CameraInfo info, ImageFolderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(info);

        Info = info;
        _options = options ?? new ImageFolderOptions();
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

    /// <summary>Folder being played (the device id).</summary>
    public string Folder => Info.DeviceId;

    /// <summary>Number of images loaded at open.</summary>
    public int ImageCount => Volatile.Read(ref _images).Length;

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

        // Decoding is CPU and disk bound; keep it off the caller (UI) thread.
        (DecodedImage[] images, PixelFormat format) = await Task.Run(() => LoadImages(null, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        lock (_sync)
        {
            _images = images;
            _pixelFormat = format;
            _width = images[0].Width;
            _height = images[0].Height;
        }

        SetState(CameraState.Open);
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (State == CameraState.Acquiring)
        {
            await StopAcquisitionAsync(cancellationToken).ConfigureAwait(false);
        }

        lock (_sync)
        {
            _images = [];
            _width = 0;
            _height = 0;
        }

        SetState(CameraState.Closed);
    }

    public Task StartAcquisitionAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_state != CameraState.Open)
            {
                throw new InvalidOperationException($"Camera is {_state}; it must be open to start acquisition.");
            }

            while (_triggers.CurrentCount > 0)
            {
                _triggers.Wait(0, CancellationToken.None);
            }

            _stopRequested = false;
            _thread = new Thread(AcquisitionLoop)
            {
                IsBackground = true,
                Name = $"ImageFolder acquisition ({Info.UserDefinedName})",
            };
            _state = CameraState.Acquiring;
            _thread.Start();
        }

        RaiseStateChanged(CameraState.Open, CameraState.Acquiring);
        return Task.CompletedTask;
    }

    public async Task StopAcquisitionAsync(CancellationToken cancellationToken = default)
    {
        Thread? thread;
        lock (_sync)
        {
            if (_state != CameraState.Acquiring)
            {
                return;
            }

            _stopRequested = true;
            thread = _thread;
            _thread = null;
        }

        _triggers.Release(); // wake a trigger wait
        if (thread is not null && thread != Thread.CurrentThread)
        {
            await Task.Run(thread.Join, cancellationToken).ConfigureAwait(false);
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

        _triggers.Release();
    }

    public IReadOnlyList<CameraParameter> GetParameters()
    {
        lock (_sync)
        {
            bool open = _state != CameraState.Closed;
            bool idle = _state == CameraState.Open;
            return
            [
                new CameraParameter(ParameterNames.ExposureTime, ParameterType.Float)
                {
                    IsWritable = true, Minimum = MinExposureUs, Maximum = MaxExposureUs, Unit = "us",
                    Description = "Simulated: scales pixel values relative to 10000 us.",
                },
                new CameraParameter(ParameterNames.Gain, ParameterType.Float)
                {
                    IsWritable = true, Minimum = 0, Maximum = MaxGainDb, Unit = "dB",
                    Description = "Simulated digital gain.",
                },
                new CameraParameter(ParameterNames.TriggerMode, ParameterType.Enumeration)
                {
                    IsWritable = true, EnumEntries = [nameof(TriggerMode.Off), nameof(TriggerMode.Software)],
                    Description = "Off = free-run at AcquisitionFrameRate; Software = one image per trigger.",
                },
                new CameraParameter(ParameterNames.TriggerSoftware, ParameterType.Command)
                {
                    IsAvailable = _state == CameraState.Acquiring && _triggerMode == TriggerMode.Software,
                    IsReadable = false,
                    IsWritable = true,
                },
                new CameraParameter(ParameterNames.AcquisitionFrameRate, ParameterType.Float)
                {
                    IsWritable = true, Minimum = MinFrameRate, Maximum = MaxFrameRate, Unit = "Hz",
                },
                new CameraParameter(ParameterNames.PixelFormat, ParameterType.Enumeration)
                {
                    IsAvailable = open, IsWritable = idle,
                    EnumEntries = [nameof(PixelFormat.Mono8), nameof(PixelFormat.Bgr8)],
                },
                new CameraParameter(ParameterNames.Width, ParameterType.Integer) { IsAvailable = open, Unit = "px" },
                new CameraParameter(ParameterNames.Height, ParameterType.Integer) { IsAvailable = open, Unit = "px" },
                new CameraParameter(LoopParameter, ParameterType.Boolean)
                {
                    IsWritable = true, Description = "Restart from the first image after the last one.",
                },
            ];
        }
    }

    public object GetParameter(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_sync)
        {
            return name switch
            {
                ParameterNames.ExposureTime => _exposureUs,
                ParameterNames.Gain => _gainDb,
                ParameterNames.TriggerMode => _triggerMode.ToString(),
                ParameterNames.AcquisitionFrameRate => _frameRate,
                ParameterNames.PixelFormat => RequireOpen(name, _pixelFormat.ToString()),
                ParameterNames.Width => RequireOpen(name, (long)_width),
                ParameterNames.Height => RequireOpen(name, (long)_height),
                LoopParameter => _loop,
                ParameterNames.TriggerSoftware => throw new InvalidOperationException($"{name} is a command; use ExecuteCommand."),
                _ => throw new KeyNotFoundException($"Unknown parameter '{name}'."),
            };
        }
    }

    public void SetParameter(string name, object value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);

        lock (_sync)
        {
            switch (name)
            {
                case ParameterNames.ExposureTime:
                    _exposureUs = InRange(name, ToDouble(value), MinExposureUs, MaxExposureUs);
                    _lut = BuildLut(_exposureUs, _gainDb);
                    break;

                case ParameterNames.Gain:
                    _gainDb = InRange(name, ToDouble(value), 0, MaxGainDb);
                    _lut = BuildLut(_exposureUs, _gainDb);
                    break;

                case ParameterNames.TriggerMode:
                    TriggerMode mode = ParseEnum<TriggerMode>(name, value);
                    if (mode == TriggerMode.Hardware)
                    {
                        throw new ArgumentException("An image folder has no hardware trigger input.", nameof(value));
                    }

                    _triggerMode = mode;
                    break;

                case ParameterNames.AcquisitionFrameRate:
                    _frameRate = InRange(name, ToDouble(value), MinFrameRate, MaxFrameRate);
                    break;

                case ParameterNames.PixelFormat:
                    if (_state != CameraState.Open)
                    {
                        throw new InvalidOperationException($"{name} can only be changed while open and not acquiring.");
                    }

                    PixelFormat format = ParseEnum<PixelFormat>(name, value);
                    if (format != _pixelFormat)
                    {
                        // Re-decode in the new layout. Done under the lock: the camera is idle.
                        (_images, _pixelFormat) = LoadImages(format, CancellationToken.None);
                    }

                    break;

                case LoopParameter:
                    _loop = value is bool b ? b : bool.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);
                    break;

                case ParameterNames.Width or ParameterNames.Height:
                    throw new InvalidOperationException($"{name} is fixed by the image files.");

                default:
                    throw new KeyNotFoundException($"Unknown parameter '{name}'.");
            }
        }
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

        _triggers.Dispose();
    }

    private (DecodedImage[] Images, PixelFormat Format) LoadImages(PixelFormat? requested, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Folder))
        {
            throw new DirectoryNotFoundException($"Image folder not found: {Folder}");
        }

        string[] files = ImageFolderProvider.EnumerateImages(Folder).ToArray();
        if (files.Length == 0)
        {
            throw new InvalidDataException($"No supported images in {Folder}.");
        }

        PixelFormat format = requested ?? DetectFormat(files[0]);
        ImreadModes mode = format == PixelFormat.Mono8 ? ImreadModes.Grayscale : ImreadModes.Color;

        var images = new DecodedImage[files.Length];
        long totalBytes = 0;
        for (int i = 0; i < files.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using Mat mat = Cv2.ImRead(files[i], mode);
            if (mat.Empty())
            {
                throw new InvalidDataException($"Cannot decode image: {files[i]}");
            }

            if (i > 0 && (mat.Width != images[0].Width || mat.Height != images[0].Height))
            {
                throw new InvalidDataException(
                    $"All images must have the same size. {Path.GetFileName(files[i])} is {mat.Width}x{mat.Height}, " +
                    $"expected {images[0].Width}x{images[0].Height}.");
            }

            int stride = mat.Width * format.BytesPerPixel();
            totalBytes += (long)stride * mat.Height;
            if (totalBytes > _options.MaxMemoryBytes)
            {
                throw new InvalidOperationException(
                    $"Images in {Folder} exceed the memory limit of {_options.MaxMemoryBytes / (1024 * 1024)} MB.");
            }

            var pixels = new byte[stride * mat.Height];
            for (int y = 0; y < mat.Height; y++)
            {
                Marshal.Copy(mat.Ptr(y), pixels, y * stride, stride);
            }

            images[i] = new DecodedImage(Path.GetFileName(files[i]), mat.Width, mat.Height, format, pixels);
        }

        return (images, format);
    }

    private static PixelFormat DetectFormat(string file)
    {
        using Mat probe = Cv2.ImRead(file, ImreadModes.Unchanged);
        return probe.Channels() == 1 ? PixelFormat.Mono8 : PixelFormat.Bgr8;
    }

    private void AcquisitionLoop()
    {
        long frameId = 0;
        int index = 0;
        bool endReported = false;
        long startTicks = Stopwatch.GetTimestamp();
        long nextDue = startTicks;

        try
        {
            while (!_stopRequested)
            {
                if (_triggerMode == TriggerMode.Software)
                {
                    if (!_triggers.Wait(PollInterval) || _stopRequested)
                    {
                        continue;
                    }
                }
                else
                {
                    long period = (long)(Stopwatch.Frequency / Volatile.Read(ref _frameRate));
                    if (!WaitUntil(nextDue))
                    {
                        continue; // poll interval elapsed; re-check stop/mode
                    }

                    // Keep the cadence, but never burst to catch up after a stall.
                    nextDue = Math.Max(nextDue + period, Stopwatch.GetTimestamp());
                }

                DecodedImage[] images = Volatile.Read(ref _images);
                if (index >= images.Length)
                {
                    if (!_loop)
                    {
                        if (!endReported)
                        {
                            endReported = true;
                            RaiseError(CameraErrorKind.Other, "End of image sequence (Loop is off).");
                        }

                        continue;
                    }

                    index = 0;
                }

                Emit(images[index++], ++frameId, Stopwatch.GetTimestamp() - startTicks);
            }
        }
#pragma warning disable CA1031 // Report any failure as a camera fault instead of crashing the process.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            RaiseError(CameraErrorKind.Other, $"Acquisition failed: {ex.Message}", ex);
            SetState(CameraState.Faulted);
        }
    }

    /// <summary>Waits until <paramref name="dueTicks"/>; returns false if it gave up early to poll.</summary>
    private bool WaitUntil(long dueTicks)
    {
        long pollLimit = Stopwatch.GetTimestamp() + (long)(PollInterval.TotalSeconds * Stopwatch.Frequency);
        while (!_stopRequested)
        {
            long now = Stopwatch.GetTimestamp();
            long remaining = dueTicks - now;
            if (remaining <= 0)
            {
                return true;
            }

            if (now >= pollLimit)
            {
                return false;
            }

            if (remaining > SpinThresholdTicks)
            {
                // Sleep resolution can be ~15 ms on Windows; sleep only the coarse part.
                Thread.Sleep(TimeSpan.FromSeconds((double)(remaining - SpinThresholdTicks) / Stopwatch.Frequency));
            }
            else
            {
                Thread.SpinWait(50);
            }
        }

        return false;
    }

    private void Emit(DecodedImage image, long frameId, long deviceTimestamp)
    {
        Frame frame = Frame.Rent(image.Width, image.Height, image.Format, frameId, DateTime.UtcNow, deviceTimestamp, pool: _pool);
        try
        {
            Span<byte> target = frame.GetWritableData();
            byte[]? lut = _lut;
            if (lut is null)
            {
                image.Pixels.CopyTo(target);
            }
            else
            {
                ReadOnlySpan<byte> source = image.Pixels;
                for (int i = 0; i < source.Length; i++)
                {
                    target[i] = lut[source[i]];
                }
            }

            FrameReceived?.Invoke(this, new FrameReceivedEventArgs(frame));
        }
#pragma warning disable CA1031 // A failing subscriber must not stop acquisition.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            RaiseError(CameraErrorKind.FrameError, $"Frame #{frameId} handler failed: {ex.Message}", ex);
        }
        finally
        {
            frame.Dispose();
        }
    }

    private static byte[]? BuildLut(double exposureUs, double gainDb)
    {
        double factor = exposureUs / ReferenceExposureUs * Math.Pow(10, gainDb / 20);
        if (Math.Abs(factor - 1) < 1e-9)
        {
            return null;
        }

        var lut = new byte[256];
        for (int i = 0; i < lut.Length; i++)
        {
            lut[i] = (byte)Math.Clamp(Math.Round(i * factor), 0, 255);
        }

        return lut;
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

    private object RequireOpen(string name, object value) =>
        _state != CameraState.Closed ? value : throw new InvalidOperationException($"{name} is available after the camera is opened.");

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

    private sealed record DecodedImage(string FileName, int Width, int Height, PixelFormat Format, byte[] Pixels);
}
