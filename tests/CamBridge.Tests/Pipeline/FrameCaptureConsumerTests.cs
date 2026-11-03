using CamBridge.Core;
using CamBridge.Pipeline;
using CamBridge.Pipeline.Saving;
using CamBridge.Pipeline.Statistics;
using CamBridge.Tests.Fakes;
using OpenCvSharp;

namespace CamBridge.Tests.Pipeline;

public sealed class FrameCaptureConsumerTests : IDisposable
{
    private static readonly DateTime Timestamp = new(2026, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc);
    private static readonly TimeSpan Patient = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Impatient = TimeSpan.FromMilliseconds(50);
    private static readonly ConsumerOptions SessionQueue = new() { Capacity = 1 };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cambridge-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static Frame MonoFrame(long id, byte value = 0)
    {
        Frame frame = Frame.Rent(8, 6, PixelFormat.Mono8, id, Timestamp);
        frame.GetWritableData().Fill(value);
        return frame;
    }

    private static FakeCamera NewCamera() => new(new CameraInfo("fake", "1", "Fake", "Model"));

    private string[] SavedFiles() => Directory.Exists(_root) ? Directory.GetFiles(_root) : [];

    [Fact]
    public async Task WithoutARequest_EveryFrameIsDeclined()
    {
        var camera = NewCamera();
        await using var pipeline = new AcquisitionPipeline(camera);
        pipeline.AddConsumer(new FrameCaptureConsumer(), SessionQueue);
        pipeline.Start();

        for (int id = 1; id <= 50; id++)
        {
            camera.Emit(MonoFrame(id));
        }

        await pipeline.StopAsync(drain: true);

        ConsumerStatistics stats = pipeline.GetStatistics().Consumers[0];
        Assert.Equal(0, stats.ProcessedFrames);
        Assert.Equal(0, stats.DroppedFrames);
        Assert.Equal(0, stats.PeakQueueLength);
        Assert.Empty(SavedFiles());
    }

    [Fact]
    public async Task Request_SavesOnlyTheNextFrame_ToAFolderItCreates()
    {
        var camera = NewCamera();
        var consumer = new FrameCaptureConsumer();
        await using var pipeline = new AcquisitionPipeline(camera);
        pipeline.AddConsumer(consumer, SessionQueue);
        pipeline.Start();

        camera.Emit(MonoFrame(1, value: 10));
        Task<string> capture = consumer.CaptureNextAsync(_root, Patient);
        camera.Emit(MonoFrame(2, value: 20));
        camera.Emit(MonoFrame(3, value: 30));
        string path = await capture;
        await pipeline.StopAsync(drain: true);

        Assert.EndsWith("_00000002.png", path, StringComparison.Ordinal);
        Assert.Equal([path], SavedFiles());
        using Mat saved = Cv2.ImRead(path, ImreadModes.Unchanged);
        Assert.Equal(new Size(8, 6), saved.Size());
        Assert.Equal(1, saved.Channels());
        Assert.Equal(20, saved.At<byte>(3, 3));

        ConsumerStatistics stats = pipeline.GetStatistics().Consumers[0];
        Assert.Equal(1, stats.ProcessedFrames);
        Assert.Equal(0, stats.DroppedFrames);
    }

    [Fact]
    public async Task Requests_InARow_EachSaveTheirOwnFrame()
    {
        var camera = NewCamera();
        var consumer = new FrameCaptureConsumer();
        await using var pipeline = new AcquisitionPipeline(camera);
        pipeline.AddConsumer(consumer, SessionQueue);
        pipeline.Start();

        for (int id = 1; id <= 3; id++)
        {
            Task<string> capture = consumer.CaptureNextAsync(Path.Combine(_root, $"class{id}"), Patient);
            camera.Emit(MonoFrame(id));
            string path = await capture;
            Assert.Equal($"class{id}", Path.GetFileName(Path.GetDirectoryName(path)));
        }

        await pipeline.StopAsync(drain: true);
        Assert.Equal(3, pipeline.GetStatistics().Consumers[0].ProcessedFrames);
    }

    [Fact]
    public async Task SecondRequest_WhileOneIsPending_IsRejected_AndTheFirstStillCompletes()
    {
        var consumer = new FrameCaptureConsumer();
        Task<string> first = consumer.CaptureNextAsync(_root, Patient);

        await Assert.ThrowsAsync<InvalidOperationException>(() => consumer.CaptureNextAsync(_root, Patient));

        using Frame frame = MonoFrame(1);
        Assert.True(consumer.Accepts(frame));
        await consumer.ProcessAsync(frame, CancellationToken.None);
        Assert.Equal(SavedFiles().Single(), await first);
    }

    [Fact]
    public async Task NoFrameInTime_TimesOut_AndTheNextRequestStillWorks()
    {
        var consumer = new FrameCaptureConsumer();

        await Assert.ThrowsAsync<TimeoutException>(() => consumer.CaptureNextAsync(_root, Impatient));

        using Frame declined = MonoFrame(1);
        Assert.False(consumer.Accepts(declined));

        Task<string> next = consumer.CaptureNextAsync(_root, Patient);
        using Frame frame = MonoFrame(2);
        Assert.True(consumer.Accepts(frame));
        await consumer.ProcessAsync(frame, CancellationToken.None);
        await next;
    }

    [Fact]
    public async Task Cancelling_ReleasesTheRequest()
    {
        var consumer = new FrameCaptureConsumer();
        using var cts = new CancellationTokenSource();
        Task<string> capture = consumer.CaptureNextAsync(_root, Patient, cts.Token);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);

        using Frame frame = MonoFrame(1);
        Assert.False(consumer.Accepts(frame));
    }

    [Fact]
    public async Task FrameQueuedForATimedOutRequest_IsNotSavedForTheNextOne()
    {
        var consumer = new FrameCaptureConsumer();
        using Frame stale = MonoFrame(1);
        using Frame fresh = MonoFrame(2);

        Task<string> timedOut = consumer.CaptureNextAsync(_root, Impatient);
        Assert.True(consumer.Accepts(stale));
        await Assert.ThrowsAsync<TimeoutException>(() => timedOut);

        Task<string> next = consumer.CaptureNextAsync(_root, Patient);
        await consumer.ProcessAsync(stale, CancellationToken.None);
        Assert.Empty(SavedFiles());
        Assert.False(next.IsCompleted);

        Assert.True(consumer.Accepts(fresh));
        await consumer.ProcessAsync(fresh, CancellationToken.None);
        Assert.EndsWith("_00000002.png", await next, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveFailure_FailsTheRequest_AndCountsAsAFault()
    {
        var camera = NewCamera();
        var consumer = new FrameCaptureConsumer(new FailingEncoder());
        await using var pipeline = new AcquisitionPipeline(camera);
        pipeline.AddConsumer(consumer, SessionQueue);
        pipeline.Start();

        Task<string> capture = consumer.CaptureNextAsync(_root, Patient);
        camera.Emit(MonoFrame(1));

        await Assert.ThrowsAsync<IOException>(() => capture);
        await pipeline.StopAsync(drain: true);

        Assert.Equal(1, pipeline.GetStatistics().Consumers[0].Faults);
        Assert.Empty(SavedFiles());
    }

    private sealed class FailingEncoder : IFrameEncoder
    {
        public byte[] Encode(Frame frame, ImageFileFormat format, int jpegQuality) => throw new IOException("disk full");
    }
}
