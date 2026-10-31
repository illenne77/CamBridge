using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace CamBridge.App.Logging;

/// <summary>
/// Writes log entries to daily files (<c>cambridge-yyyyMMdd.log</c>). Logging threads only
/// enqueue; a background task does the file I/O, so camera and pipeline threads never block on disk.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int QueueCapacity = 10_000;

    private readonly string _directory;
    private readonly LogLevel _minimumLevel;
    private readonly Channel<LogEntry> _queue = Channel.CreateBounded<LogEntry>(
        new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly Task _writer;

    public FileLoggerProvider(string directory, LogLevel minimumLevel = LogLevel.Information)
    {
        _directory = directory;
        _minimumLevel = minimumLevel;
        Directory.CreateDirectory(directory);
        _writer = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) =>
        new EntryLogger(categoryName, entry => _queue.Writer.TryWrite(entry), _minimumLevel);

    /// <summary>Flushes queued entries and closes the file.</summary>
    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(2));
    }

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        DateOnly day = default;
        try
        {
            await foreach (LogEntry entry in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                DateOnly entryDay = DateOnly.FromDateTime(entry.Timestamp);
                if (writer is null || entryDay != day)
                {
                    if (writer is not null)
                    {
                        await writer.DisposeAsync().ConfigureAwait(false);
                    }

                    day = entryDay;
                    string path = Path.Combine(_directory, string.Create(CultureInfo.InvariantCulture, $"cambridge-{day:yyyyMMdd}.log"));
                    writer = new StreamWriter(path, append: true, Encoding.UTF8);
                }

                await writer.WriteLineAsync(entry.Format()).ConfigureAwait(false);
                if (_queue.Reader.Count == 0)
                {
                    await writer.FlushAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
