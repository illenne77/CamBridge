using CamBridge.Core;

namespace CamBridge.Pipeline;

/// <summary>
/// Holds only the most recent frame for display. Publishing replaces (and disposes) a frame
/// that was never taken, so a slow UI skips frames instead of slowing acquisition.
/// </summary>
public sealed class LatestFrameSlot : IDisposable
{
    private Frame? _frame;
    private long _published;
    private long _skipped;
    private int _disposed;

    /// <summary>Frames published so far.</summary>
    public long PublishedFrames => Interlocked.Read(ref _published);

    /// <summary>Frames replaced before anyone took them.</summary>
    public long SkippedFrames => Interlocked.Read(ref _skipped);

    /// <summary>Stores <paramref name="frame"/>, taking ownership of the handle.</summary>
    public void Publish(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        Frame? previous = Interlocked.Exchange(ref _frame, frame);
        Interlocked.Increment(ref _published);
        if (previous is not null)
        {
            previous.Dispose();
            Interlocked.Increment(ref _skipped);
        }

        // A publish racing with Dispose must not leave a frame behind.
        if (Volatile.Read(ref _disposed) != 0)
        {
            Interlocked.Exchange(ref _frame, null)?.Dispose();
        }
    }

    /// <summary>Takes the latest frame, or null if none arrived since the last take. The caller disposes it.</summary>
    public Frame? TryTake() => Interlocked.Exchange(ref _frame, null);

    public void Dispose()
    {
        Volatile.Write(ref _disposed, 1);
        Interlocked.Exchange(ref _frame, null)?.Dispose();
    }
}
