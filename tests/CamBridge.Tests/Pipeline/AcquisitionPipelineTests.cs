using CamBridge.Core;
using CamBridge.Pipeline;
using CamBridge.Pipeline.Statistics;
using CamBridge.Tests.Fakes;

namespace CamBridge.Tests.Pipeline;

public sealed class AcquisitionPipelineTests : IAsyncDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private readonly TrackingArrayPool _pool = new();
    private readonly FakeCamera _camera = new(new CameraInfo("fake", "1", "Fake", "Model"));
    private readonly AcquisitionPipeline _pipeline;

    public AcquisitionPipelineTests()
    {
        _pipeline = new AcquisitionPipeline(_camera);
    }

    public async ValueTask DisposeAsync()
    {
        await _pipeline.DisposeAsync();
    }

    private void Emit(long id) =>
        _camera.Emit(Frame.Rent(8, 4, PixelFormat.Mono8, id, DateTime.UtcNow, pool: _pool));

    private void EmitRange(long first, long last)
    {
        for (long id = first; id <= last; id++)
        {
            Emit(id);
        }
    }

    [Fact]
    public async Task Frames_ReachEveryConsumerAndLiveView_InOrder()
    {
        var a = new RecordingConsumer("A");
        var b = new RecordingConsumer("B");
        _pipeline.AddConsumer(a, new ConsumerOptions { Capacity = 16 });
        _pipeline.AddConsumer(b, new ConsumerOptions { Capacity = 16 });
        _pipeline.Start();

        EmitRange(1, 5);
        await _pipeline.StopAsync(drain: true);

        Assert.Equal([1L, 2, 3, 4, 5], a.Ids);
        Assert.Equal([1L, 2, 3, 4, 5], b.Ids);
        using (Frame? live = _pipeline.LiveView.TryTake())
        {
            Assert.Equal(5, live!.FrameId);
        }

        PipelineStatistics stats = _pipeline.GetStatistics();
        Assert.Equal(5, stats.ReceivedFrames);
        Assert.Equal(4, stats.LiveViewSkippedFrames);
        Assert.Equal(0, stats.TotalDroppedFrames);
        Assert.All(stats.Consumers, c => Assert.Equal(5, c.ProcessedFrames));
        Assert.Equal(0, _pool.Outstanding);
    }

    [Fact]
    public async Task DropOldest_KeepsMostRecentFrames()
    {
        using var gate = new SemaphoreSlim(0);
        var consumer = new RecordingConsumer { Gate = gate };
        _pipeline.AddConsumer(consumer, new ConsumerOptions { Capacity = 2, OverflowPolicy = QueueOverflowPolicy.DropOldest });
        _pipeline.Start();

        Emit(1);
        await consumer.FirstFrameStarted.Task.WaitAsync(TestTimeout);
        EmitRange(2, 10);
        Assert.Equal(2, _pipeline.GetStatistics().Consumers[0].QueueLength);

        gate.Release(int.MaxValue / 2);
        await _pipeline.StopAsync(drain: true);

        Assert.Equal([1L, 9, 10], consumer.Ids);
        ConsumerStatistics stats = _pipeline.GetStatistics().Consumers[0];
        Assert.Equal(7, stats.DroppedFrames);
        Assert.Equal(10, stats.ProcessedFrames + stats.DroppedFrames);
    }

    [Fact]
    public async Task DeclinedFrames_TakeNoQueueSlot_AndAreNotDrops()
    {
        using var gate = new SemaphoreSlim(0);
        var consumer = new RecordingConsumer { Gate = gate, Filter = frame => frame.FrameId % 10 == 1 };
        _pipeline.AddConsumer(consumer, new ConsumerOptions { Capacity = 2, OverflowPolicy = QueueOverflowPolicy.DropOldest });
        _pipeline.Start();

        Emit(1);
        await consumer.FirstFrameStarted.Task.WaitAsync(TestTimeout);
        EmitRange(2, 21); // only 11 and 21 are accepted, which exactly fills the queue

        gate.Release(int.MaxValue / 2);
        await _pipeline.StopAsync(drain: true);
        _pipeline.LiveView.TryTake()?.Dispose();

        Assert.Equal([1L, 11, 21], consumer.Ids);
        PipelineStatistics stats = _pipeline.GetStatistics();
        Assert.Equal(21, stats.ReceivedFrames);
        Assert.Equal(3, stats.Consumers[0].ProcessedFrames);
        Assert.Equal(0, stats.TotalDroppedFrames);
        Assert.Equal(0, _pool.Outstanding);
    }

    [Fact]
    public async Task DropNewest_KeepsQueuedFrames()
    {
        using var gate = new SemaphoreSlim(0);
        var consumer = new RecordingConsumer { Gate = gate };
        _pipeline.AddConsumer(consumer, new ConsumerOptions { Capacity = 2, OverflowPolicy = QueueOverflowPolicy.DropNewest });
        _pipeline.Start();

        Emit(1);
        await consumer.FirstFrameStarted.Task.WaitAsync(TestTimeout);
        EmitRange(2, 10);

        gate.Release(int.MaxValue / 2);
        await _pipeline.StopAsync(drain: true);

        Assert.Equal([1L, 2, 3], consumer.Ids);
        Assert.Equal(7, _pipeline.GetStatistics().Consumers[0].DroppedFrames);
    }

    [Fact]
    public async Task Wait_BlocksCameraThreadUntilSpaceFrees()
    {
        using var gate = new SemaphoreSlim(0);
        var consumer = new RecordingConsumer { Gate = gate };
        _pipeline.AddConsumer(consumer, new ConsumerOptions
        {
            Capacity = 1,
            OverflowPolicy = QueueOverflowPolicy.Wait,
            WaitTimeout = TestTimeout,
        });
        _pipeline.Start();

        Emit(1);
        await consumer.FirstFrameStarted.Task.WaitAsync(TestTimeout);
        Emit(2);
        Task blocked = Task.Run(() => Emit(3));

        await Task.Delay(100);
        Assert.False(blocked.IsCompleted);

        gate.Release(int.MaxValue / 2);
        await blocked.WaitAsync(TestTimeout);
        await _pipeline.StopAsync(drain: true);

        Assert.Equal([1L, 2, 3], consumer.Ids);
        Assert.Equal(0, _pipeline.GetStatistics().Consumers[0].DroppedFrames);
    }

    [Fact]
    public async Task Wait_DropsFrameAfterTimeout()
    {
        using var gate = new SemaphoreSlim(0);
        var consumer = new RecordingConsumer { Gate = gate };
        _pipeline.AddConsumer(consumer, new ConsumerOptions
        {
            Capacity = 1,
            OverflowPolicy = QueueOverflowPolicy.Wait,
            WaitTimeout = TimeSpan.FromMilliseconds(30),
        });
        _pipeline.Start();

        Emit(1);
        await consumer.FirstFrameStarted.Task.WaitAsync(TestTimeout);
        Emit(2);
        Emit(3);

        gate.Release(int.MaxValue / 2);
        await _pipeline.StopAsync(drain: true);

        Assert.Equal([1L, 2], consumer.Ids);
        Assert.Equal(1, _pipeline.GetStatistics().Consumers[0].DroppedFrames);
    }

    [Fact]
    public async Task CameraDrops_AreDetectedFromFrameIdGaps()
    {
        _pipeline.Start();

        foreach (long id in new long[] { 1, 2, 5, 6, 10 })
        {
            Emit(id);
        }

        Emit(1); // counter restart is not a drop
        Emit(2);
        await _pipeline.StopAsync();

        PipelineStatistics stats = _pipeline.GetStatistics();
        Assert.Equal(7, stats.ReceivedFrames);
        Assert.Equal(5, stats.CameraDroppedFrames);
    }

    private void ReportLost(long id) =>
        _camera.RaiseError(new CameraErrorEventArgs(CameraErrorKind.FrameError, "lost", lostFrameId: id));

    [Fact]
    public async Task LostFrames_CountAtOnce_EvenIfNoGoodFrameFollows()
    {
        _pipeline.Start();

        ReportLost(1);
        ReportLost(2);
        ReportLost(3);

        PipelineStatistics stats = _pipeline.GetStatistics();
        Assert.Equal(0, stats.ReceivedFrames);
        Assert.Equal(3, stats.CameraDroppedFrames);
        Assert.Equal(3, stats.TotalDroppedFrames);
        await _pipeline.StopAsync();
    }

    [Fact]
    public async Task LostFrames_AreNotCountedAgain_ByTheNextGap()
    {
        _pipeline.Start();

        Emit(1);
        ReportLost(2);
        ReportLost(3);
        Emit(4);
        Emit(6); // 5 vanished without a report
        ReportLost(8); // 7 vanished too, before the frame reported lost
        Emit(9);
        await _pipeline.StopAsync();

        PipelineStatistics stats = _pipeline.GetStatistics();
        Assert.Equal(4, stats.ReceivedFrames);
        Assert.Equal(5, stats.CameraDroppedFrames); // 2, 3, 5, 7, 8
    }

    [Fact]
    public async Task LostFrame_ReportedAfterALaterFrame_IsNotCountedTwice()
    {
        _pipeline.Start();

        Emit(1);
        Emit(3);
        ReportLost(2);
        Emit(4);
        await _pipeline.StopAsync();

        Assert.Equal(1, _pipeline.GetStatistics().CameraDroppedFrames);
    }

    [Fact]
    public async Task FrameErrorWithoutALostFrame_IsNotADrop()
    {
        _pipeline.Start();

        Emit(1);
        _camera.RaiseError(new CameraErrorEventArgs(CameraErrorKind.FrameError, "a handler failed"));
        Emit(2);
        await _pipeline.StopAsync();

        Assert.Equal(0, _pipeline.GetStatistics().CameraDroppedFrames);
    }

    [Fact]
    public async Task LostFrames_AfterStop_AreNotCounted()
    {
        _pipeline.Start();
        await _pipeline.StopAsync();

        ReportLost(1);

        Assert.Equal(0, _pipeline.GetStatistics().CameraDroppedFrames);
    }

    [Fact]
    public async Task PeakQueueLength_AndMaxProcessingTime_OutliveTheStall()
    {
        using var gate = new SemaphoreSlim(0);
        var consumer = new RecordingConsumer { Gate = gate };
        _pipeline.AddConsumer(consumer, new ConsumerOptions { Capacity = 4, OverflowPolicy = QueueOverflowPolicy.DropOldest });
        _pipeline.Start();

        Emit(1);
        await consumer.FirstFrameStarted.Task.WaitAsync(TestTimeout);
        EmitRange(2, 4);
        await Task.Delay(50); // the first frame is stuck in the consumer at least this long

        gate.Release(int.MaxValue / 2);
        await _pipeline.StopAsync(drain: true);

        ConsumerStatistics stats = _pipeline.GetStatistics().Consumers[0];
        Assert.Equal(0, stats.QueueLength);
        Assert.Equal(3, stats.PeakQueueLength);
        Assert.InRange(stats.MaxProcessingTime, TimeSpan.FromMilliseconds(40), TestTimeout);

        _pipeline.Start();
        Assert.Equal(0, _pipeline.GetStatistics().Consumers[0].PeakQueueLength);
        Assert.Equal(TimeSpan.Zero, _pipeline.GetStatistics().Consumers[0].MaxProcessingTime);
        await _pipeline.StopAsync();
    }

    [Fact]
    public async Task FailingConsumer_IsCountedAndKeepsRunning()
    {
        var consumer = new RecordingConsumer
        {
            OnFrame = f => f.FrameId % 2 == 0 ? throw new InvalidOperationException($"bad {f.FrameId}") : Task.CompletedTask,
        };
        _pipeline.AddConsumer(consumer, new ConsumerOptions { Capacity = 16 });
        _pipeline.Start();

        EmitRange(1, 6);
        await _pipeline.StopAsync(drain: true);

        ConsumerStatistics stats = _pipeline.GetStatistics().Consumers[0];
        Assert.Equal([1L, 3, 5], consumer.Ids);
        Assert.Equal(3, stats.ProcessedFrames);
        Assert.Equal(3, stats.Faults);
        Assert.Equal("bad 6", stats.LastError);

        _pipeline.LiveView.TryTake()?.Dispose();
        Assert.Equal(0, _pool.Outstanding);
    }

    [Fact]
    public async Task StopWithoutDrain_DiscardsQueueAndReturnsAllBuffers()
    {
        using var gate = new SemaphoreSlim(0);
        var consumer = new RecordingConsumer { Gate = gate };
        _pipeline.AddConsumer(consumer, new ConsumerOptions { Capacity = 4 });
        _pipeline.Start();

        Emit(1);
        await consumer.FirstFrameStarted.Task.WaitAsync(TestTimeout);
        EmitRange(2, 5);

        await _pipeline.StopAsync(drain: false).WaitAsync(TestTimeout);
        _pipeline.LiveView.TryTake()?.Dispose();

        Assert.Empty(consumer.Ids);
        Assert.Equal(0, _pipeline.GetStatistics().Consumers[0].DroppedFrames);
        Assert.Equal(0, _pool.Outstanding);
    }

    [Fact]
    public void AddConsumer_WhileRunning_Throws()
    {
        _pipeline.Start();

        Assert.Throws<InvalidOperationException>(() => _pipeline.AddConsumer(new RecordingConsumer()));
    }

    [Fact]
    public async Task Restart_ResetsStatistics()
    {
        _pipeline.AddConsumer(new RecordingConsumer());
        _pipeline.Start();
        EmitRange(1, 3);
        await _pipeline.StopAsync(drain: true);

        _pipeline.Start();
        Emit(10);
        await _pipeline.StopAsync(drain: true);

        PipelineStatistics stats = _pipeline.GetStatistics();
        Assert.Equal(1, stats.ReceivedFrames);
        Assert.Equal(0, stats.CameraDroppedFrames);
        Assert.Equal(1, stats.Consumers[0].ProcessedFrames);
    }

    [Fact]
    public async Task Soak_HighRateWithSlowConsumer_NoLeakAndExactAccounting()
    {
        var fast = new RecordingConsumer("Fast");
        var slow = new RecordingConsumer("Slow") { OnFrame = _ => Task.Delay(1) };
        _pipeline.AddConsumer(fast, new ConsumerOptions { Capacity = 64 });
        _pipeline.AddConsumer(slow, new ConsumerOptions { Capacity = 4 });
        _pipeline.Start();

        long emitted = 0;
        var producer = new Thread(() =>
        {
            var until = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < until)
            {
                Emit(++emitted);
            }
        });
        producer.Start();
        producer.Join();

        await _pipeline.StopAsync(drain: true).WaitAsync(TestTimeout);
        PipelineStatistics stats = _pipeline.GetStatistics();
        _pipeline.LiveView.TryTake()?.Dispose();

        Assert.Equal(emitted, stats.ReceivedFrames);
        Assert.Equal(0, stats.CameraDroppedFrames);
        Assert.All(stats.Consumers, c =>
        {
            Assert.Equal(emitted, c.ProcessedFrames + c.DroppedFrames);
            Assert.Equal(0, c.QueueLength);
        });
        Assert.True(stats.Consumers[1].DroppedFrames > 0, "slow consumer should have dropped frames");
        Assert.Equal(emitted, _pool.Rented);
        Assert.Equal(0, _pool.Outstanding);
    }
}
