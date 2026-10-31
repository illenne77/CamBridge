namespace CamBridge.Pipeline.Statistics;

/// <summary>
/// Events per second over a sliding one-second window made of ten 100 ms buckets.
/// </summary>
public sealed class RateMeter
{
    private const int BucketCount = 10;
    private static readonly TimeSpan BucketWidth = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Window = BucketWidth * BucketCount;

    private readonly TimeProvider _timeProvider;
    private readonly long[] _counts = new long[BucketCount];
    private readonly long[] _bucketNumbers = new long[BucketCount];
    private readonly object _lock = new();
    private long _startTimestamp;

    public RateMeter(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        Reset();
    }

    public void Reset()
    {
        lock (_lock)
        {
            Array.Clear(_counts);
            Array.Fill(_bucketNumbers, -1);
            _startTimestamp = _timeProvider.GetTimestamp();
        }
    }

    public void Record(int count = 1)
    {
        lock (_lock)
        {
            long bucket = CurrentBucket(out _);
            int slot = (int)(bucket % BucketCount);
            if (_bucketNumbers[slot] != bucket)
            {
                _bucketNumbers[slot] = bucket;
                _counts[slot] = 0;
            }

            _counts[slot] += count;
        }
    }

    public double GetRate()
    {
        lock (_lock)
        {
            long current = CurrentBucket(out TimeSpan elapsed);
            long sum = 0;
            for (int i = 0; i < BucketCount; i++)
            {
                long bucket = _bucketNumbers[i];
                if (bucket > current - BucketCount && bucket <= current)
                {
                    sum += _counts[i];
                }
            }

            // The window spans the nine completed buckets plus the elapsed part of the current
            // one, or less right after a reset.
            TimeSpan inCurrent = elapsed - (BucketWidth * current);
            TimeSpan span = elapsed < Window ? elapsed : Window - BucketWidth + inCurrent;
            return span > TimeSpan.Zero ? sum / span.TotalSeconds : 0;
        }
    }

    private long CurrentBucket(out TimeSpan elapsed)
    {
        elapsed = _timeProvider.GetElapsedTime(_startTimestamp);
        return elapsed.Ticks / BucketWidth.Ticks;
    }
}
