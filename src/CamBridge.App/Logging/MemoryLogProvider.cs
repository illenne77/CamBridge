using Microsoft.Extensions.Logging;

namespace CamBridge.App.Logging;

/// <summary>
/// Keeps the most recent entries for the UI log strip. The UI polls <see cref="Version"/> and
/// takes a <see cref="Snapshot"/> only when it changed, so logging never posts to the dispatcher.
/// </summary>
public sealed class MemoryLogProvider(int capacity = 200, LogLevel minimumLevel = LogLevel.Information) : ILoggerProvider
{
    private readonly Queue<LogEntry> _entries = new(capacity);
    private readonly object _sync = new();
    private long _version;

    /// <summary>Incremented on every new entry.</summary>
    public long Version => Interlocked.Read(ref _version);

    public ILogger CreateLogger(string categoryName) => new EntryLogger(categoryName, Add, minimumLevel);

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_sync)
        {
            return [.. _entries];
        }
    }

    public void Dispose()
    {
    }

    private void Add(LogEntry entry)
    {
        lock (_sync)
        {
            if (_entries.Count == capacity)
            {
                _entries.Dequeue();
            }

            _entries.Enqueue(entry);
        }

        Interlocked.Increment(ref _version);
    }
}
