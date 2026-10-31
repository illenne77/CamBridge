using CamBridge.Pipeline.Statistics;
using CamBridge.Tests.Fakes;

namespace CamBridge.Tests.Pipeline;

public class RateMeterTests
{
    [Fact]
    public void SteadyRate_IsMeasuredOverSlidingWindow()
    {
        var time = new ManualTimeProvider();
        var meter = new RateMeter(time);

        for (int i = 0; i < 100; i++)
        {
            meter.Record();
            time.Advance(TimeSpan.FromMilliseconds(20));
        }

        Assert.Equal(50, meter.GetRate(), precision: 6);
    }

    [Fact]
    public void FirstSecond_UsesElapsedTime()
    {
        var time = new ManualTimeProvider();
        var meter = new RateMeter(time);

        for (int i = 0; i < 10; i++)
        {
            meter.Record();
            time.Advance(TimeSpan.FromMilliseconds(50));
        }

        Assert.Equal(20, meter.GetRate(), precision: 6);
    }

    [Fact]
    public void Idle_DecaysToZero()
    {
        var time = new ManualTimeProvider();
        var meter = new RateMeter(time);
        meter.Record(100);

        time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(0, meter.GetRate());
    }

    [Fact]
    public void Reset_ClearsHistory()
    {
        var time = new ManualTimeProvider();
        var meter = new RateMeter(time);
        meter.Record(100);
        time.Advance(TimeSpan.FromMilliseconds(500));

        meter.Reset();
        time.Advance(TimeSpan.FromMilliseconds(500));

        Assert.Equal(0, meter.GetRate());
    }
}
