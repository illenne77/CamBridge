using CamBridge.App.Logging;

namespace CamBridge.Tests.Windows.App;

public sealed class LogThrottleTests
{
    private sealed class ManualClock : TimeProvider
    {
        public long Milliseconds { get; set; }

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Milliseconds;
    }

    [Fact]
    public void FirstPasses_ThenOnePerInterval_ReportingWhatWasHeldBack()
    {
        var clock = new ManualClock();
        var throttle = new LogThrottle(TimeSpan.FromSeconds(1), clock);

        Assert.True(throttle.ShouldLog(out long skipped));
        Assert.Equal(0, skipped);

        Assert.False(throttle.ShouldLog(out _));
        clock.Milliseconds = 999;
        Assert.False(throttle.ShouldLog(out _));

        clock.Milliseconds = 1000;
        Assert.True(throttle.ShouldLog(out skipped));
        Assert.Equal(2, skipped);

        clock.Milliseconds = 2000;
        Assert.True(throttle.ShouldLog(out skipped));
        Assert.Equal(0, skipped);
    }
}
