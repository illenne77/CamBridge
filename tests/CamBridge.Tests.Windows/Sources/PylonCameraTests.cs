using System.Collections.Concurrent;
using CamBridge.Core;
using CamBridge.Sources.Pylon;
using CamBridge.Tests.Fakes;

namespace CamBridge.Tests.Windows.Sources;

/// <summary>
/// Fact that runs against the pylon camera emulator. Skipped when this build has no pylon
/// support or the pylon runtime cannot be loaded. Uses only the public API, so it compiles
/// without the SDK.
/// </summary>
public sealed class PylonEmulatorFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> SkipReason = new(() =>
    {
        // Must be set before pylon's transport layers are first loaded in this process.
        Environment.SetEnvironmentVariable("PYLON_CAMEMU", "2");
        if (PylonProviderFactory.TryCreate(null, out string? reason) is not { } provider)
        {
            return reason;
        }

        try
        {
            IReadOnlyList<CameraInfo> cameras = provider.EnumerateAsync().GetAwaiter().GetResult();
            return cameras.Any(c => c.Interface == CameraInterface.Emulated) ? null : "No pylon emulator cameras.";
        }
#pragma warning disable CA1031 // Any failure means the environment cannot run these tests.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return $"pylon unavailable: {ex.Message}";
        }
    });

    public PylonEmulatorFactAttribute()
    {
        if (SkipReason.Value is string reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>Emulator tests share one emulated device and therefore one class (no parallel access).</summary>
[Collection("Pylon")]
public sealed class PylonCameraTests : IAsyncLifetime
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    private readonly TrackingArrayPool _pool = new();
    private ICameraProvider? _provider;
    private ICamera? _camera;

    private ICamera Camera => _camera!;

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("PYLON_CAMEMU", "2");
        _provider = PylonProviderFactory.TryCreate(new PylonOptions { BufferPool = _pool }, out _);
        if (_provider is null)
        {
            return;
        }

        try
        {
            IReadOnlyList<CameraInfo> cameras = await _provider.EnumerateAsync();
            CameraInfo? emulated = cameras.FirstOrDefault(c => c.Interface == CameraInterface.Emulated);
            if (emulated is not null)
            {
                _camera = _provider.Create(emulated);
            }
        }
        catch (InvalidOperationException)
        {
            // pylon runtime missing: every test is skipped by the attribute.
        }
    }

    public async Task DisposeAsync()
    {
        if (_camera is not null)
        {
            await _camera.DisposeAsync();
        }
    }

    /// <summary>Small frames at a high rate keep the tests fast.</summary>
    private async Task OpenSmallAsync()
    {
        await Camera.OpenAsync();
        Camera.SetParameter(ParameterNames.PixelFormat, "Mono8");
        Camera.SetParameter(ParameterNames.Width, 320L);
        Camera.SetParameter(ParameterNames.Height, 240L);
        Camera.ExposureTimeUs = 1000;
        Camera.SetParameter(ParameterNames.AcquisitionFrameRate, 200.0);
        Camera.TriggerMode = TriggerMode.Off;
    }

    [PylonEmulatorFact]
    public async Task Provider_ListsEmulatedCamerasWithStableIds()
    {
        IReadOnlyList<CameraInfo> cameras = await _provider!.EnumerateAsync();
        CameraInfo[] emulated = cameras.Where(c => c.Interface == CameraInterface.Emulated).ToArray();

        Assert.True(emulated.Length >= 2);
        Assert.All(emulated, c => Assert.Equal(PylonProviderFactory.ProviderId, c.ProviderId));
        Assert.All(emulated, c => Assert.False(string.IsNullOrEmpty(c.SerialNumber)));
        Assert.Equal(emulated.Length, emulated.Select(c => c.DeviceId).Distinct().Count());
        Assert.Equal("Basler pylon", _provider.VendorName);
    }

    [PylonEmulatorFact]
    public async Task Parameters_DescribeVendorNeutralAndGenericNodes()
    {
        await Camera.OpenAsync();
        IReadOnlyList<CameraParameter> parameters = Camera.GetParameters();

        CameraParameter exposure = Assert.Single(parameters, p => p.Name == ParameterNames.ExposureTime);
        Assert.Equal(ParameterType.Float, exposure.Type);
        Assert.Equal("us", exposure.Unit);
        Assert.True(exposure.Minimum > 0 && exposure.Maximum > exposure.Minimum);

        CameraParameter trigger = Assert.Single(parameters, p => p.Name == ParameterNames.TriggerMode);
        Assert.Equal(["Off", "Software", "Hardware"], trigger.EnumEntries);

        CameraParameter testImage = Assert.Single(parameters, p => p.Name == "TestImageSelector");
        Assert.Contains("Off", testImage.EnumEntries);
        Assert.Equal(parameters.Count, parameters.Select(p => p.Name).Distinct().Count());

        Assert.Throws<KeyNotFoundException>(() => Camera.GetParameter("NoSuchFeature"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Camera.ExposureTimeUs = exposure.Maximum!.Value * 10);
        Assert.Throws<ArgumentException>(() => Camera.SetParameter("TestImageSelector", "NoSuchImage"));
    }

    [PylonEmulatorFact]
    public async Task Parameters_EveryListedReadableNameCanBeRead()
    {
        await Camera.OpenAsync();

        foreach (CameraParameter p in Camera.GetParameters().Where(p => p is { IsAvailable: true, IsReadable: true } && p.Type != ParameterType.Command))
        {
            try
            {
                Assert.NotNull(Camera.GetParameter(p.Name));
            }
            catch (InvalidOperationException)
            {
                // allowed: became unreadable through another setting
            }
        }
    }

    [PylonEmulatorFact]
    public async Task Parameters_RoundTripTypedAndGenericValues()
    {
        await Camera.OpenAsync();

        Camera.ExposureTimeUs = 5000;
        Camera.SetParameter("TestImageSelector", "Testimage1");
        Camera.SetParameter("ReverseX", true);

        Assert.Equal(5000, Camera.ExposureTimeUs, precision: 0);
        Assert.Equal("Testimage1", Camera.GetParameter("TestImageSelector"));
        Assert.Equal(true, Camera.GetParameter("ReverseX"));
        Assert.IsType<double>(Camera.GetParameter(ParameterNames.Gain));
        Camera.SetParameter("ReverseX", false);
    }

    [PylonEmulatorFact]
    public async Task FreeRun_FrameIdsAreBlockIdsWithoutGaps_AndBuffersAreReturned()
    {
        var frames = new ConcurrentQueue<(long Id, int Width, PixelFormat Format)>();
        Camera.FrameReceived += (_, e) => frames.Enqueue((e.Frame.FrameId, e.Frame.Width, e.Frame.PixelFormat));
        await OpenSmallAsync();

        await Camera.StartAcquisitionAsync();
        await WaitForAsync(() => frames.Count >= 50);
        await Camera.StopAcquisitionAsync();
        await Camera.CloseAsync();

        long[] ids = frames.Select(f => f.Id).ToArray();
        Assert.All(ids.Zip(ids.Skip(1)), pair => Assert.Equal(pair.First + 1, pair.Second));
        Assert.All(frames, f => Assert.Equal((320, PixelFormat.Mono8), (f.Width, f.Format)));
        Assert.Equal(0, _pool.Outstanding);
    }

    [PylonEmulatorFact]
    public async Task SoftwareTrigger_OneFramePerTrigger()
    {
        var ids = new ConcurrentQueue<long>();
        Camera.FrameReceived += (_, e) => ids.Enqueue(e.Frame.FrameId);
        await OpenSmallAsync();
        Camera.TriggerMode = TriggerMode.Software;
        Assert.Equal("Software", Camera.GetParameter(ParameterNames.TriggerMode));

        await Camera.StartAcquisitionAsync();
        await Task.Delay(300);
        Assert.Empty(ids);

        for (int i = 1; i <= 3; i++)
        {
            Camera.ExecuteSoftwareTrigger();
            await WaitForAsync(() => ids.Count >= i);
        }

        await Task.Delay(200);
        await Camera.StopAcquisitionAsync();
        Assert.Equal(3, ids.Count);
    }

    [PylonEmulatorFact]
    public async Task HardwareTrigger_SelectsInputLine_AndWaitsForIt()
    {
        int frames = 0;
        Camera.FrameReceived += (_, _) => Interlocked.Increment(ref frames);
        await OpenSmallAsync();

        Camera.TriggerMode = TriggerMode.Hardware;
        await Camera.StartAcquisitionAsync();
        await Task.Delay(300);
        await Camera.StopAcquisitionAsync();

        Assert.Equal("Hardware", Camera.GetParameter(ParameterNames.TriggerMode));
        Assert.StartsWith("Line", (string)Camera.GetParameter("TriggerSource"), StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref frames));
        Assert.Throws<InvalidOperationException>(Camera.ExecuteSoftwareTrigger);
        Camera.TriggerMode = TriggerMode.Off;
    }

    [PylonEmulatorFact]
    public async Task PixelFormat_LockedWhileAcquiring_AndBayerIsConvertedToBgr()
    {
        var formats = new ConcurrentQueue<(PixelFormat Format, int Stride, int Width)>();
        Camera.FrameReceived += (_, e) => formats.Enqueue((e.Frame.PixelFormat, e.Frame.Stride, e.Frame.Width));
        await OpenSmallAsync();
        Camera.SetParameter(ParameterNames.PixelFormat, "BayerRG8");

        await Camera.StartAcquisitionAsync();
        await WaitForAsync(() => formats.Count >= 3);
        Assert.Throws<InvalidOperationException>(() => Camera.SetParameter(ParameterNames.PixelFormat, "Mono8"));
        await Camera.StopAcquisitionAsync();
        Camera.SetParameter(ParameterNames.PixelFormat, "Mono8");

        Assert.All(formats, f => Assert.Equal((PixelFormat.Bgr8, 320 * 3), (f.Format, f.Stride)));
    }

    [PylonEmulatorFact]
    public async Task Open_ReplacesAnUnsupportedPixelFormatWithMono8()
    {
        await Camera.OpenAsync();
        Camera.SetParameter(ParameterNames.PixelFormat, "Mono12");
        await Camera.CloseAsync();

        await Camera.OpenAsync();

        Assert.Equal("Mono8", Camera.GetParameter(ParameterNames.PixelFormat));
    }

    [PylonEmulatorFact]
    public async Task AcquisitionFrameRate_EnablesTheRateAndAppliesIt()
    {
        await Camera.OpenAsync();
        Camera.SetParameter("AcquisitionFrameRateEnable", false);

        Camera.SetParameter(ParameterNames.AcquisitionFrameRate, 50.0);

        Assert.Equal(true, Camera.GetParameter("AcquisitionFrameRateEnable"));
        Assert.Equal(50.0, Assert.IsType<double>(Camera.GetParameter(ParameterNames.AcquisitionFrameRate)), precision: 3);
    }

    [PylonEmulatorFact]
    public async Task StreamStatistics_AreReadOnlyParameters_WhenTheTransportHasThem()
    {
        await Camera.OpenAsync();

        foreach (CameraParameter p in Camera.GetParameters().Where(p => p.Name.StartsWith("Statistic_", StringComparison.Ordinal)))
        {
            Assert.Equal(ParameterType.Integer, p.Type);
            Assert.IsType<long>(Camera.GetParameter(p.Name));
        }

        Assert.Throws<KeyNotFoundException>(() => Camera.GetParameter("Statistic_NoSuchCounter"));
    }

    [PylonEmulatorFact]
    public async Task StateTransitions_FollowTheCameraContract()
    {
        var states = new ConcurrentQueue<CameraState>();
        Camera.StateChanged += (_, e) => states.Enqueue(e.NewState);

        Assert.Throws<InvalidOperationException>(() => Camera.GetParameter(ParameterNames.ExposureTime));
        await OpenSmallAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Camera.OpenAsync());
        await Camera.StartAcquisitionAsync();
        await Camera.CloseAsync(); // stops acquisition first

        Assert.Equal([CameraState.Open, CameraState.Acquiring, CameraState.Open, CameraState.Closed], states);
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
