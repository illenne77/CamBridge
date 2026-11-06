using CamBridge.Core;
using CamBridge.Pipeline;
using CamBridge.Pipeline.Statistics;
using CamBridge.Tests.Fakes;

namespace CamBridge.Tests.Pipeline;

public sealed class ConsumerWarmUpTests : IAsyncDisposable
{
    private readonly FakeCamera _camera = new(new CameraInfo("fake", "1", "Fake", "Model"));
    private readonly AcquisitionPipeline _pipeline;

    public ConsumerWarmUpTests()
    {
        _camera.SetParameter(ParameterNames.Width, 64L);
        _camera.SetParameter(ParameterNames.Height, 48L);
        _pipeline = new AcquisitionPipeline(_camera);
    }

    public async ValueTask DisposeAsync() => await _pipeline.DisposeAsync();

    [Fact]
    public void EveryConsumer_GetsBlankMono8AndBgr8Samples_OfTheCameraSize()
    {
        var a = new WarmUpRecorder("A");
        var b = new WarmUpRecorder("B");
        _pipeline.AddConsumer(a);
        _pipeline.AddConsumer(b);

        IReadOnlyList<ConsumerWarmUp> results = _pipeline.WarmUp();

        Assert.Equal(["A", "B"], results.Select(r => r.Consumer));
        Assert.All(results, r => Assert.Null(r.Error));
        foreach (WarmUpRecorder consumer in new[] { a, b })
        {
            Assert.Equal([(PixelFormat.Mono8, 64, 48, true), (PixelFormat.Bgr8, 64, 48, true)], consumer.Samples);
        }
    }

    [Fact]
    public async Task WarmUp_LeavesStatisticsUntouched_AndFramesFlowAfterStart()
    {
        var consumer = new WarmUpRecorder("A");
        _pipeline.AddConsumer(consumer, new ConsumerOptions { Capacity = 4 });

        _pipeline.WarmUp();
        PipelineStatistics afterWarmUp = _pipeline.GetStatistics();
        _pipeline.Start();
        _camera.Emit(Frame.Rent(64, 48, PixelFormat.Mono8, 1, DateTime.UtcNow));
        await _pipeline.StopAsync(drain: true);

        Assert.Equal(0, afterWarmUp.ReceivedFrames);
        Assert.Equal(0, afterWarmUp.Consumers[0].ProcessedFrames);
        Assert.Equal([1L], consumer.ProcessedIds);
        Assert.Equal(1, _pipeline.GetStatistics().Consumers[0].ProcessedFrames);
    }

    [Fact]
    public void FailingConsumer_IsReported_AndTheOthersStillWarmUp()
    {
        var failing = new WarmUpRecorder("Bad") { Fail = true };
        var good = new WarmUpRecorder("Good");
        _pipeline.AddConsumer(failing);
        _pipeline.AddConsumer(good);

        IReadOnlyList<ConsumerWarmUp> results = _pipeline.WarmUp();

        Assert.Equal("warm-up failed", results[0].Error);
        Assert.Null(results[1].Error);
        Assert.Equal(2, failing.Samples.Count); // the second format is still tried
        Assert.Equal(2, good.Samples.Count);
    }

    [Fact]
    public void ConsumerWithoutWarmUp_IsTimedAsANoOp()
    {
        _pipeline.AddConsumer(new RecordingConsumer("Plain"));

        ConsumerWarmUp result = Assert.Single(_pipeline.WarmUp());

        Assert.Equal("Plain", result.Consumer);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task WithoutTheCameraSize_WarmUpIsSkipped()
    {
        var camera = new FakeCamera(new CameraInfo("fake", "2", "Fake", "Model")); // no Width or Height
        await using var pipeline = new AcquisitionPipeline(camera);
        var consumer = new WarmUpRecorder("A");
        pipeline.AddConsumer(consumer);

        Assert.Empty(pipeline.WarmUp());
        Assert.Empty(consumer.Samples);
    }

    [Fact]
    public void WarmUp_WhileRunning_Throws()
    {
        _pipeline.AddConsumer(new WarmUpRecorder("A"));
        _pipeline.Start();

        Assert.Throws<InvalidOperationException>(() => _pipeline.WarmUp());
    }

    private sealed class WarmUpRecorder(string name) : IFrameConsumer
    {
        public string Name { get; } = name;

        public bool Fail { get; init; }

        public List<(PixelFormat Format, int Width, int Height, bool Blank)> Samples { get; } = [];

        /// <summary>Written by the worker; read only after the pipeline has stopped.</summary>
        public List<long> ProcessedIds { get; } = [];

        public ValueTask ProcessAsync(Frame frame, CancellationToken cancellationToken)
        {
            ProcessedIds.Add(frame.FrameId);
            return ValueTask.CompletedTask;
        }

        public void WarmUp(Frame sample)
        {
            Samples.Add((sample.PixelFormat, sample.Width, sample.Height, sample.Memory.Span.IndexOfAnyExcept((byte)0) < 0));
            if (Fail)
            {
                throw new InvalidOperationException("warm-up failed");
            }
        }
    }
}
