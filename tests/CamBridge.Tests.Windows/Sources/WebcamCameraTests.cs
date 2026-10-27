using System.Collections.Concurrent;
using CamBridge.Core;
using CamBridge.Sources.Webcam;
using CamBridge.Sources.Webcam.Interop;
using CamBridge.Tests.Fakes;

namespace CamBridge.Tests.Windows.Sources;

/// <summary>Fact that runs only when a DirectShow video capture device is connected.</summary>
public sealed class WebcamFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> HasWebcam = new(() => DirectShowDevices.Enumerate().Count > 0);

    public WebcamFactAttribute()
    {
        if (!HasWebcam.Value)
        {
            Skip = "No webcam connected.";
        }
    }
}

/// <summary>Camera behavior without a device: state checks and parameter validation.</summary>
public sealed class WebcamCameraClosedTests
{
    private static WebcamCamera Camera() =>
        new(new CameraInfo(WebcamProvider.Id, "missing-device", "Webcam", "Missing") { Interface = CameraInterface.Webcam });

    [Fact]
    public async Task Closed_DescribesTypedParametersAsUnavailable()
    {
        await using WebcamCamera camera = Camera();

        IReadOnlyList<CameraParameter> parameters = camera.GetParameters();

        Assert.False(camera.FindParameter(ParameterNames.ExposureTime)!.IsAvailable);
        Assert.False(camera.FindParameter(ParameterNames.Gain)!.IsAvailable);
        Assert.False(camera.FindParameter(ParameterNames.AcquisitionFrameRate)!.IsAvailable);
        Assert.Contains(parameters, p => p.Name == ParameterNames.TriggerMode && p.IsWritable);
        Assert.Throws<InvalidOperationException>(() => camera.ExposureTimeUs);
    }

    [Fact]
    public async Task Parameters_ValidateNamesAndValues()
    {
        await using WebcamCamera camera = Camera();

        Assert.Throws<KeyNotFoundException>(() => camera.GetParameter("NoSuchFeature"));
        Assert.Throws<KeyNotFoundException>(() => camera.SetParameter("NoSuchFeature", 1));
        Assert.Throws<ArgumentException>(() => camera.TriggerMode = TriggerMode.Hardware);
        Assert.Throws<ArgumentException>(() => camera.SetParameter(ParameterNames.PixelFormat, "Rgb16"));
        Assert.Throws<InvalidOperationException>(() => camera.GetParameter(ParameterNames.AcquisitionFrameRate));

        camera.TriggerMode = TriggerMode.Software;
        camera.SetParameter(ParameterNames.PixelFormat, "mono8");
        Assert.Equal("Software", camera.GetParameter(ParameterNames.TriggerMode));
        Assert.Equal("Mono8", camera.GetParameter(ParameterNames.PixelFormat));
    }

    [Fact]
    public async Task Open_MissingDevice_ThrowsAndStaysClosed()
    {
        await using WebcamCamera camera = Camera();

        await Assert.ThrowsAsync<InvalidOperationException>(() => camera.OpenAsync());
        Assert.Equal(CameraState.Closed, camera.State);
    }
}

/// <summary>Tests against the first connected webcam. Kept in one class so they never share the device concurrently.</summary>
public sealed class WebcamCameraDeviceTests : IAsyncLifetime
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    private readonly TrackingArrayPool _pool = new();
    private WebcamCamera? _camera;

    private WebcamCamera Camera => _camera!;

    public async Task InitializeAsync()
    {
        IReadOnlyList<CameraInfo> cameras = await new WebcamProvider().EnumerateAsync();
        if (cameras.Count > 0)
        {
            _camera = (WebcamCamera)new WebcamProvider(new WebcamOptions { BufferPool = _pool }).Create(cameras[0]);
        }
    }

    public async Task DisposeAsync()
    {
        if (_camera is not null)
        {
            if (_camera.State != CameraState.Closed && _camera.FindParameter("ExposureAuto") is { IsAvailable: true })
            {
                _camera.SetParameter("ExposureAuto", "Continuous"); // leave the webcam as found
            }

            await _camera.DisposeAsync();
        }
    }

    [WebcamFact]
    public async Task Provider_EnumeratesDevicePathAsDeviceId()
    {
        CameraInfo info = Camera.Info;

        Assert.Equal(WebcamProvider.Id, info.ProviderId);
        Assert.Equal(CameraInterface.Webcam, info.Interface);
        Assert.False(string.IsNullOrWhiteSpace(info.Model));
        Assert.StartsWith(@"\\?\", info.DeviceId, StringComparison.Ordinal);

        await using ICamera created = new WebcamProvider().Create(info);
        Assert.IsType<WebcamCamera>(created);
    }

    [WebcamFact]
    public void IsPresent_MatchesConnectedDeviceOnly()
    {
        Assert.True(DirectShowDevices.IsPresent(Camera.Info.DeviceId));
        Assert.True(DirectShowDevices.IsPresent(Camera.Info.DeviceId.ToUpperInvariant()));
        Assert.False(DirectShowDevices.IsPresent(@"\\?\usb#vid_0000&pid_0000#missing"));
    }

    [WebcamFact]
    public async Task OpenClose_TransitionsStateAndDescribesControls()
    {
        var states = new ConcurrentQueue<CameraState>();
        Camera.StateChanged += (_, e) => states.Enqueue(e.NewState);

        await Camera.OpenAsync();
        CameraParameter exposure = Camera.FindParameter(ParameterNames.ExposureTime)!;
        long width = (long)Camera.GetParameter(ParameterNames.Width);
        await Camera.CloseAsync();

        Assert.Equal([CameraState.Open, CameraState.Closed], states);
        Assert.True(exposure.IsAvailable);
        Assert.True(exposure.Minimum > 0 && exposure.Maximum > exposure.Minimum);
        Assert.True(width > 0);
    }

    [WebcamFact]
    public async Task FreeRun_DeliversConsecutiveFramesAndReturnsBuffers()
    {
        var frames = new ConcurrentQueue<(long Id, int Width, PixelFormat Format)>();
        Camera.FrameReceived += (_, e) => frames.Enqueue((e.Frame.FrameId, e.Frame.Width, e.Frame.PixelFormat));

        await Camera.OpenAsync();
        await Camera.StartAcquisitionAsync();
        await WaitForAsync(() => frames.Count >= 10);
        await Camera.StopAcquisitionAsync();
        int countAtStop = frames.Count;
        await Task.Delay(200);
        await Camera.CloseAsync();

        long[] ids = frames.Select(f => f.Id).ToArray();
        Assert.Equal(Enumerable.Range(1, ids.Length).Select(i => (long)i), ids);
        Assert.All(frames, f => Assert.Equal(PixelFormat.Bgr8, f.Format));
        Assert.Equal(countAtStop, frames.Count);
        Assert.Equal(0, _pool.Outstanding);
    }

    [WebcamFact]
    public async Task SoftwareTrigger_PublishesOneFramePerTrigger()
    {
        var ids = new ConcurrentQueue<long>();
        Camera.FrameReceived += (_, e) => ids.Enqueue(e.Frame.FrameId);
        Camera.TriggerMode = TriggerMode.Software;

        await Camera.OpenAsync();
        await Camera.StartAcquisitionAsync();
        await Task.Delay(500);
        Assert.Empty(ids);

        for (int i = 1; i <= 3; i++)
        {
            Camera.ExecuteSoftwareTrigger();
            await WaitForAsync(() => ids.Count >= i);
        }

        await Task.Delay(300);
        await Camera.CloseAsync();

        Assert.Equal([1L, 2L, 3L], ids);
    }

    [WebcamFact]
    public async Task Mono8_IsConvertedOnHost()
    {
        var formats = new ConcurrentQueue<(PixelFormat Format, int Stride, int Width)>();
        Camera.FrameReceived += (_, e) => formats.Enqueue((e.Frame.PixelFormat, e.Frame.Stride, e.Frame.Width));
        Camera.SetParameter(ParameterNames.PixelFormat, nameof(PixelFormat.Mono8));

        await Camera.OpenAsync();
        await Camera.StartAcquisitionAsync();
        await WaitForAsync(() => !formats.IsEmpty);
        Assert.Throws<InvalidOperationException>(() => Camera.SetParameter(ParameterNames.PixelFormat, nameof(PixelFormat.Bgr8)));
        await Camera.CloseAsync();

        (PixelFormat format, int stride, int width) = formats.First();
        Assert.Equal(PixelFormat.Mono8, format);
        Assert.Equal(width, stride);
    }

    [WebcamFact]
    public async Task ManualExposure_ChangesBrightnessAndIsLockedWhileAuto()
    {
        await Camera.OpenAsync();
        if (Camera.FindParameter("ExposureAuto") is not { IsAvailable: true })
        {
            return; // device without auto exposure: nothing to switch
        }

        Camera.SetParameter("ExposureAuto", "Continuous");
        Assert.Throws<InvalidOperationException>(() => Camera.ExposureTimeUs = 10_000);
        Assert.False(Camera.FindParameter(ParameterNames.ExposureTime)!.IsWritable);

        Camera.SetParameter("ExposureAuto", "Off");
        CameraParameter exposure = Camera.FindParameter(ParameterNames.ExposureTime)!;
        Assert.True(exposure.IsWritable);

        double latest = 0;
        Camera.FrameReceived += (_, e) => Volatile.Write(ref latest, Mean(e.Frame));
        await Camera.StartAcquisitionAsync();

        Camera.ExposureTimeUs = exposure.Minimum!.Value;
        double shortExposure = await SettledMeanAsync(() => Volatile.Read(ref latest));
        Assert.Equal(exposure.Minimum.Value, Camera.ExposureTimeUs, precision: 3);

        Camera.ExposureTimeUs = Math.Min(exposure.Maximum!.Value, exposure.Minimum.Value * 64);
        double longExposure = await SettledMeanAsync(() => Volatile.Read(ref latest));

        Assert.True(longExposure > shortExposure + 10, $"mean {shortExposure:F1} -> {longExposure:F1}");
        Assert.Throws<ArgumentOutOfRangeException>(() => Camera.ExposureTimeUs = exposure.Maximum.Value * 2);
    }

    [WebcamFact]
    public async Task UnsupportedOrGenericControls_FollowParameterContract()
    {
        await Camera.OpenAsync();
        IReadOnlyList<CameraParameter> parameters = Camera.GetParameters();

        if (!Camera.FindParameter(ParameterNames.Gain)!.IsAvailable)
        {
            Assert.Throws<InvalidOperationException>(() => Camera.Gain);
        }

        CameraParameter? brightness = parameters.FirstOrDefault(p => p.Name == "Brightness");
        if (brightness is { IsWritable: true })
        {
            long original = (long)Camera.GetParameter("Brightness");
            Camera.SetParameter("Brightness", (long)brightness.Minimum!.Value);
            Assert.Equal((long)brightness.Minimum.Value, Camera.GetParameter("Brightness"));
            Camera.SetParameter("Brightness", original);
            Assert.Throws<ArgumentOutOfRangeException>(() => Camera.SetParameter("Brightness", brightness.Maximum!.Value + 1));
        }
    }

    [WebcamFact]
    public async Task Resolution_CanBeChangedWhileIdleOnly()
    {
        int width = 0;
        Camera.FrameReceived += (_, e) => Volatile.Write(ref width, e.Frame.Width);

        await Camera.OpenAsync();
        long original = (long)Camera.GetParameter(ParameterNames.Width);
        long target = original == 640 ? 320 : 640;
        Camera.SetParameter(ParameterNames.Width, target);
        Camera.SetParameter(ParameterNames.Height, target * 3 / 4);
        long actual = (long)Camera.GetParameter(ParameterNames.Width);

        await Camera.StartAcquisitionAsync();
        await WaitForAsync(() => Volatile.Read(ref width) != 0);
        Assert.Throws<InvalidOperationException>(() => Camera.SetParameter(ParameterNames.Width, original));
        await Camera.StopAcquisitionAsync();

        Assert.Equal(actual, Volatile.Read(ref width));
        Camera.SetParameter(ParameterNames.Width, original);
        Camera.SetParameter(ParameterNames.Height, original * 3 / 4);
    }

    private static double Mean(Frame frame)
    {
        long sum = 0;
        ReadOnlySpan<byte> data = frame.Data;
        for (int i = 0; i < data.Length; i += 7)
        {
            sum += data[i];
        }

        return sum / (double)((data.Length + 6) / 7);
    }

    /// <summary>Waits for the exposure change to reach the sensor (a few frames), then samples.</summary>
    private static async Task<double> SettledMeanAsync(Func<double> latest)
    {
        await Task.Delay(800);
        return latest();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        while (!condition())
        {
            await Task.Delay(10, cts.Token);
        }
    }
}
