using System.Collections.Concurrent;
using System.Diagnostics;
using CamBridge.Core;
using CamBridge.Pipeline;
using CamBridge.Pipeline.Statistics;
using CamBridge.Sources.Pylon;
using CamBridge.Tests.Fakes;
using Xunit.Abstractions;

namespace CamBridge.Tests.Windows.Sources;

/// <summary>Fact that runs only with a real Basler camera connected (GigE or USB3, not the emulator).</summary>
public sealed class PylonRealCameraFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> SkipReason = new(() =>
    {
        if (PylonProviderFactory.TryCreate(null, out string? reason) is not { } provider)
        {
            return reason;
        }

        try
        {
            return provider.EnumerateAsync().GetAwaiter().GetResult().Any(PylonRealCameraTests.IsReal)
                ? null
                : "No real Basler camera connected.";
        }
#pragma warning disable CA1031 // Any failure means the environment cannot run these tests.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return $"pylon unavailable: {ex.Message}";
        }
    });

    public PylonRealCameraFactAttribute()
    {
        if (SkipReason.Value is string reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>
/// What the emulator cannot prove: SFNC 1.x node names, GigE block IDs and a real transport.
/// Run with: dotnet test tests/CamBridge.Tests.Windows --filter "FullyQualifiedName~PylonRealCamera"
/// --logger "console;verbosity=detailed" to see which nodes and counters the camera reports.
/// Disconnecting is checked by hand (see docs/PLAN.md), since it needs someone to pull the cable.
/// </summary>
[Collection("Pylon")]
public sealed class PylonRealCameraTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    private readonly TrackingArrayPool _pool = new();
    private ICamera? _camera;

    private ICamera Camera => _camera!;

    internal static bool IsReal(CameraInfo info) => info.Interface is CameraInterface.GigE or CameraInterface.Usb3;

    public async Task InitializeAsync()
    {
        if (PylonProviderFactory.TryCreate(new PylonOptions { BufferPool = _pool }, out _) is not { } provider)
        {
            return;
        }

        try
        {
            CameraInfo? info = (await provider.EnumerateAsync()).FirstOrDefault(IsReal);
            if (info is not null)
            {
                _camera = provider.Create(info);
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

    /// <summary>A known starting point: settings can survive from another tool while the camera stays powered.</summary>
    private async Task OpenManualAsync()
    {
        await Camera.OpenAsync();
        SetOffIfPresent(ParameterNames.ExposureAuto);
        SetOffIfPresent(ParameterNames.GainAuto);
        Camera.TriggerMode = TriggerMode.Off;
        Camera.SetParameter(ParameterNames.PixelFormat, "Mono8");
        Camera.ExposureTimeUs = 1000;
    }

    private void SetOffIfPresent(string name)
    {
        if (Camera.FindParameter(name) is { IsWritable: true })
        {
            Camera.SetParameter(name, "Off");
        }
    }

    [PylonRealCameraFact]
    public async Task Identity_ReportsCameraAndWhichNodesImplementTheVendorNeutralNames()
    {
        await Camera.OpenAsync();

        CameraInfo info = Camera.Info;
        output.WriteLine($"{info.DisplayName} [{info.Interface}] {info.DeviceId}");
        foreach (string name in new[] { ParameterNames.ExposureTime, ParameterNames.Gain, ParameterNames.AcquisitionFrameRate })
        {
            CameraParameter p = Camera.FindParameter(name)!;
            output.WriteLine($"{name}: value {Camera.GetParameter(name)} range {p.Minimum}..{p.Maximum} {p.Unit} ({p.Description})");
            Assert.True(p.IsAvailable, $"{name} has no node on this camera.");
        }

        output.WriteLine($"Size {Camera.GetParameter(ParameterNames.Width)}x{Camera.GetParameter(ParameterNames.Height)}, format {Camera.GetParameter(ParameterNames.PixelFormat)}");
        output.WriteLine($"Pixel formats: {string.Join(", ", Camera.FindParameter(ParameterNames.PixelFormat)!.EnumEntries)}");
        output.WriteLine($"Listed parameters: {Camera.GetParameters().Count}");

        Assert.False(string.IsNullOrEmpty(info.Model));
        Assert.False(string.IsNullOrEmpty(info.SerialNumber));
    }

    [PylonRealCameraFact]
    public async Task EveryListedReadableName_CanBeRead()
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

    [PylonRealCameraFact]
    public async Task VendorNeutralNames_RoundTripThroughTheCamerasNodes()
    {
        await OpenManualAsync();

        Camera.ExposureTimeUs = 5000;
        output.WriteLine($"ExposureTime 5000 -> {Camera.ExposureTimeUs}");
        Assert.InRange(Camera.ExposureTimeUs, 4750, 5250); // cameras quantize to their time base

        CameraParameter gain = Camera.FindParameter(ParameterNames.Gain)!;
        Camera.Gain = gain.Minimum!.Value;
        output.WriteLine($"Gain min {gain.Minimum} {gain.Unit} -> {Camera.Gain}");
        Assert.Equal(gain.Minimum.Value, Camera.Gain, precision: 3);
        Assert.IsType<double>(Camera.GetParameter(ParameterNames.Gain));

        Camera.SetParameter(ParameterNames.AcquisitionFrameRate, 30.0);
        double rate = Convert.ToDouble(Camera.GetParameter(ParameterNames.AcquisitionFrameRate), System.Globalization.CultureInfo.InvariantCulture);
        output.WriteLine($"AcquisitionFrameRate 30 -> {rate}");
        Assert.InRange(rate, 29, 31);

        Assert.Throws<ArgumentOutOfRangeException>(() => Camera.ExposureTimeUs = Camera.FindParameter(ParameterNames.ExposureTime)!.Maximum!.Value * 2);
        Assert.Throws<KeyNotFoundException>(() => Camera.GetParameter("NoSuchFeature"));
    }

    [PylonRealCameraFact]
    public async Task FreeRun_HasNoGapsOrErrors_AtTheRequestedRate()
    {
        const double RequestedFps = 100;
        const int Seconds = 5;
        var ids = new ConcurrentQueue<long>();
        var errors = new ConcurrentQueue<string>();
        Camera.FrameReceived += (_, e) => ids.Enqueue(e.Frame.FrameId);
        Camera.ErrorOccurred += (_, e) => errors.Enqueue($"{e.Kind}: {e.Message}");
        await OpenManualAsync();
        Camera.SetParameter(ParameterNames.AcquisitionFrameRate, RequestedFps);

        await Camera.StartAcquisitionAsync();
        var clock = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(Seconds));
        if (Camera.Info.Interface == CameraInterface.GigE)
        {
            output.WriteLine($"Packet size {Camera.GetParameter("GevSCPSPacketSize")}");
        }

        WriteTransportStatistics();
        await Camera.StopAcquisitionAsync();
        double fps = ids.Count / clock.Elapsed.TotalSeconds;
        await Camera.CloseAsync();

        long[] frames = ids.ToArray();
        long gaps = frames.Zip(frames.Skip(1)).Sum(pair => pair.Second - pair.First - 1);
        output.WriteLine($"{frames.Length} frames at {fps:0.0} fps, ids {frames.FirstOrDefault()}..{frames.LastOrDefault()}, gaps {gaps}, errors {errors.Count}");
        foreach ((long before, long after) in frames.Zip(frames.Skip(1)).Where(pair => pair.Second != pair.First + 1).Take(5))
        {
            output.WriteLine($"gap after id {before}: {after - before - 1} missing");
        }

        foreach (string error in errors.Take(5))
        {
            output.WriteLine(error);
        }

        Assert.NotEmpty(frames);
        if (Camera.Info.Interface == CameraInterface.GigE)
        {
            Assert.True(frames[0] >= 1, "GigE block IDs start at 1; 0 would mean the host counter or a wrong source.");
        }

        Assert.Empty(errors);
        Assert.Equal(0, gaps);
        Assert.InRange(fps, RequestedFps * 0.9, RequestedFps * 1.1);
        Assert.Equal(0, _pool.Outstanding);
    }

    [PylonRealCameraFact]
    public async Task PixelFormatEntries_CanAllBeSet_AndKeepTheCurrentValueWhileAcquiring()
    {
        await OpenManualAsync();

        IReadOnlyList<string> entries = Camera.FindParameter(ParameterNames.PixelFormat)!.EnumEntries;
        output.WriteLine($"Pixel formats: {string.Join(", ", entries)}");
        Assert.NotEmpty(entries);
        foreach (string entry in entries)
        {
            Camera.SetParameter(ParameterNames.PixelFormat, entry);
            Assert.Equal(entry, Camera.GetParameter(ParameterNames.PixelFormat));
        }

        Camera.SetParameter(ParameterNames.PixelFormat, "Mono8");
        await Camera.StartAcquisitionAsync();
        IReadOnlyList<string> whileAcquiring = Camera.FindParameter(ParameterNames.PixelFormat)!.EnumEntries;
        await Camera.StopAcquisitionAsync();

        output.WriteLine($"While acquiring: {string.Join(", ", whileAcquiring)}");
        Assert.Contains("Mono8", whileAcquiring);
    }

    [PylonRealCameraFact]
    public async Task PacketSize_IsNegotiatedAtStart_UnlessSetByName()
    {
        const string PacketSize = "GevSCPSPacketSize";
        const long Small = 576;
        await OpenManualAsync();
        if (Camera.Info.Interface != CameraInterface.GigE)
        {
            return;
        }

        Camera.SetParameter(PacketSize, Small);
        await Camera.StartAcquisitionAsync();
        Assert.Equal(Small, Camera.GetParameter(PacketSize));
        await Camera.StopAcquisitionAsync();
        await Camera.CloseAsync();

        await Camera.OpenAsync();
        await Camera.StartAcquisitionAsync();
        long negotiated = (long)Camera.GetParameter(PacketSize);
        await Camera.StopAcquisitionAsync();

        output.WriteLine($"Negotiated packet size: {negotiated}");
        Assert.True(negotiated > Small, $"Expected a larger packet than {Small}, got {negotiated}.");
    }

    /// <summary>
    /// A socket buffer far below one frame overflows in the host: real packet loss with no cable to
    /// disturb. How much is lost varies from run to run, so tests check the accounting, not an amount.
    /// Only the socket driver has this buffer; packet sizes here need jumbo frames on the NIC.
    /// </summary>
    private async Task OpenWithMinimalSocketBufferAsync(long packetSize)
    {
        await OpenManualAsync();
        Camera.SetParameter("StreamType", "SocketDriver");
        Camera.SetParameter("SocketBufferSize", 1L);
        Camera.SetParameter("GevSCPSPacketSize", packetSize);
        Camera.SetParameter(ParameterNames.AcquisitionFrameRate, 120.0);
    }

    private sealed record LossRun(long Received, long Dropped, long Reported, long Failed, long IdRange);

    private async Task<LossRun> AcquireThroughPipelineAsync()
    {
        var ids = new ConcurrentQueue<long>();
        long reported = 0;
        void OnFrame(object? sender, FrameReceivedEventArgs e) => ids.Enqueue(e.Frame.FrameId);
        void OnError(object? sender, CameraErrorEventArgs e)
        {
            if (e.LostFrameId is long id)
            {
                ids.Enqueue(id);
                Interlocked.Increment(ref reported);
            }
        }

        Camera.FrameReceived += OnFrame;
        Camera.ErrorOccurred += OnError;
        await using var pipeline = new AcquisitionPipeline(Camera);
        pipeline.Start();
        await Camera.StartAcquisitionAsync();
        await Task.Delay(TimeSpan.FromSeconds(5));
        WriteTransportStatistics();
        long failed = (long)Camera.GetParameter("Statistic_Failed_Buffer_Count");
        await Camera.StopAcquisitionAsync();
        await pipeline.StopAsync();
        Camera.FrameReceived -= OnFrame;
        Camera.ErrorOccurred -= OnError;

        PipelineStatistics stats = pipeline.GetStatistics();
        var run = new LossRun(stats.ReceivedFrames, stats.CameraDroppedFrames, Volatile.Read(ref reported), failed, ids.IsEmpty ? 0 : ids.Max() - ids.Min() + 1);
        output.WriteLine(run.ToString());
        return run;
    }

    [PylonRealCameraFact]
    public async Task SocketOverflow_ShowsAsResends_InTheTransportCounters()
    {
        await OpenWithMinimalSocketBufferAsync(packetSize: 9000);
        if (Camera.Info.Interface != CameraInterface.GigE)
        {
            return;
        }

        int frames = 0;
        Camera.FrameReceived += (_, _) => Interlocked.Increment(ref frames);
        await Camera.StartAcquisitionAsync();
        await Task.Delay(TimeSpan.FromSeconds(5));
        WriteTransportStatistics();
        long resends = (long)Camera.GetParameter("Statistic_Resend_Request_Count");
        await Camera.StopAcquisitionAsync();

        Assert.True(resends > 0, "Expected the overflowing socket buffer to cause resend requests.");
        Assert.True(Volatile.Read(ref frames) > 0, "Expected frames to arrive despite the resends.");
    }

    [PylonRealCameraFact]
    public async Task SomeFramesLost_EveryIdIsEitherReceivedOrDropped()
    {
        await OpenWithMinimalSocketBufferAsync(packetSize: 6000);
        if (Camera.Info.Interface != CameraInterface.GigE)
        {
            return;
        }

        LossRun run = await AcquireThroughPipelineAsync();
        for (int attempt = 1; attempt < 4 && run.Reported == 0; attempt++)
        {
            run = await AcquireThroughPipelineAsync(); // about one 5 s run in six loses nothing here
        }

        Assert.True(run.Reported > 0, "Expected failed grabs at this packet size.");
        Assert.Equal(run.IdRange, run.Received + run.Dropped);
    }

    [PylonRealCameraFact]
    public async Task EveryFrameLost_StillCountsAsDrops()
    {
        await OpenWithMinimalSocketBufferAsync(packetSize: 4500);
        if (Camera.Info.Interface != CameraInterface.GigE)
        {
            return;
        }

        LossRun run = await AcquireThroughPipelineAsync();

        // Gaps alone would read 0 here: no good frame ever closes them.
        Assert.True(run.Dropped > 0, "Expected failed grabs to count as drops at this packet size.");
        Assert.Equal(run.IdRange, run.Received + run.Dropped);
    }

    [PylonRealCameraFact]
    public async Task StreamSettings_AreListedAndLockWhileAcquiring()
    {
        await OpenManualAsync();
        if (Camera.Info.Interface != CameraInterface.GigE)
        {
            return;
        }

        CameraParameter type = Camera.FindParameter("StreamType")!;
        output.WriteLine($"Stream types: {string.Join(", ", type.EnumEntries)}, current {Camera.GetParameter("StreamType")}");
        Assert.Contains("SocketDriver", type.EnumEntries);

        Camera.SetParameter("StreamType", "SocketDriver");
        Assert.Equal("SocketDriver", Camera.GetParameter("StreamType"));
        Camera.SetParameter("SocketBufferSize", 2048L);
        Assert.Equal(2048L, Camera.GetParameter("SocketBufferSize"));

        await Camera.StartAcquisitionAsync();
        Assert.Throws<InvalidOperationException>(() => Camera.SetParameter("StreamType", "WindowsFilterDriver"));
        await Camera.StopAcquisitionAsync();
    }

    [PylonRealCameraFact]
    public async Task SoftwareTrigger_OneFramePerTrigger()
    {
        var ids = new ConcurrentQueue<long>();
        Camera.FrameReceived += (_, e) => ids.Enqueue(e.Frame.FrameId);
        await OpenManualAsync();
        Camera.TriggerMode = TriggerMode.Software;

        await Camera.StartAcquisitionAsync();
        await Task.Delay(300);
        Assert.Empty(ids);

        for (int i = 1; i <= 5; i++)
        {
            Camera.ExecuteSoftwareTrigger();
            await WaitForAsync(() => ids.Count >= i);
        }

        await Task.Delay(200);
        await Camera.StopAcquisitionAsync();
        Camera.TriggerMode = TriggerMode.Off;

        Assert.Equal(5, ids.Count);
    }

    [PylonRealCameraFact]
    public async Task HardwareTrigger_WaitsForTheInputLine()
    {
        int frames = 0;
        Camera.FrameReceived += (_, _) => Interlocked.Increment(ref frames);
        await OpenManualAsync();

        Camera.TriggerMode = TriggerMode.Hardware;
        await Camera.StartAcquisitionAsync();
        await Task.Delay(500);
        await Camera.StopAcquisitionAsync();
        output.WriteLine($"TriggerSource {Camera.GetParameter("TriggerSource")}");
        Camera.TriggerMode = TriggerMode.Off;

        Assert.Equal(0, Volatile.Read(ref frames));
    }

    private void WriteTransportStatistics()
    {
        foreach (CameraParameter p in Camera.GetParameters().Where(p => p.Name.StartsWith("Statistic_", StringComparison.Ordinal)))
        {
            output.WriteLine($"{p.Name} = {Camera.GetParameter(p.Name)}");
        }
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
