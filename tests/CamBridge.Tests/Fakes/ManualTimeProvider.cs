namespace CamBridge.Tests.Fakes;

/// <summary>Time provider whose clock only moves when the test advances it.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan delta) => Interlocked.Add(ref _ticks, delta.Ticks);
}
