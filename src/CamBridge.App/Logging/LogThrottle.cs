namespace CamBridge.App.Logging;

/// <summary>Lets one occurrence per interval of a repeating event through to the log.</summary>
public sealed class LogThrottle(TimeSpan interval, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _sync = new();
    private bool _hasLogged;
    private long _lastLogged;
    private long _skipped;

    /// <summary>True when this occurrence should be logged; <paramref name="skipped"/> is how many were held back since the last one.</summary>
    public bool ShouldLog(out long skipped)
    {
        lock (_sync)
        {
            long now = _time.GetTimestamp();
            if (_hasLogged && _time.GetElapsedTime(_lastLogged, now) < interval)
            {
                _skipped++;
                skipped = 0;
                return false;
            }

            _hasLogged = true;
            _lastLogged = now;
            skipped = _skipped;
            _skipped = 0;
            return true;
        }
    }
}
