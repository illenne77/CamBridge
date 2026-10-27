namespace CamBridge.Pipeline;

public sealed record ConsumerOptions
{
    /// <summary>Maximum number of queued frames (excluding the one being processed).</summary>
    public int Capacity { get; init; } = 4;

    public QueueOverflowPolicy OverflowPolicy { get; init; } = QueueOverflowPolicy.DropOldest;

    /// <summary>Longest time the camera thread may block under <see cref="QueueOverflowPolicy.Wait"/>.</summary>
    public TimeSpan WaitTimeout { get; init; } = TimeSpan.FromMilliseconds(100);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Capacity);
        ArgumentOutOfRangeException.ThrowIfLessThan(WaitTimeout, TimeSpan.Zero);
    }
}
