using CamBridge.Core;
using CamBridge.Pipeline;
using CamBridge.Tests.Fakes;

namespace CamBridge.Tests.Pipeline;

public class LatestFrameSlotTests
{
    private readonly TrackingArrayPool _pool = new();

    private Frame NewFrame(long id) => Frame.Rent(2, 2, PixelFormat.Mono8, id, DateTime.UtcNow, pool: _pool);

    [Fact]
    public void Publish_ReplacesUntakenFrameAndCountsSkip()
    {
        using var slot = new LatestFrameSlot();

        slot.Publish(NewFrame(1));
        slot.Publish(NewFrame(2));
        slot.Publish(NewFrame(3));

        using Frame? latest = slot.TryTake();
        Assert.Equal(3, latest!.FrameId);
        Assert.Equal(3, slot.PublishedFrames);
        Assert.Equal(2, slot.SkippedFrames);
        Assert.Equal(2, _pool.Returned);
    }

    [Fact]
    public void TryTake_ReturnsNullUntilNextPublish()
    {
        using var slot = new LatestFrameSlot();
        slot.Publish(NewFrame(1));

        slot.TryTake()!.Dispose();

        Assert.Null(slot.TryTake());
        Assert.Equal(0, slot.SkippedFrames);
    }

    [Fact]
    public void Dispose_ReleasesHeldFrame_AndLaterPublishes()
    {
        var slot = new LatestFrameSlot();
        slot.Publish(NewFrame(1));

        slot.Dispose();
        slot.Publish(NewFrame(2));

        Assert.Null(slot.TryTake());
        Assert.Equal(0, _pool.Outstanding);
    }
}
