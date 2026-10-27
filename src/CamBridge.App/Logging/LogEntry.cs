using System.Globalization;
using Microsoft.Extensions.Logging;

namespace CamBridge.App.Logging;

public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Category, string Message, Exception? Exception)
{
    /// <summary>Category without its namespace, e.g. "AcquisitionPipeline".</summary>
    public string ShortCategory => Category[(Category.LastIndexOf('.') + 1)..];

    public string LevelShort => LevelText(Level);

    public string Format() =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{LevelText(Level)}] {ShortCategory}: {Message}{(Exception is null ? string.Empty : Environment.NewLine + Exception)}");

    public static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "---",
    };
}

/// <summary>Logger that turns each call into a <see cref="LogEntry"/> for a sink.</summary>
internal sealed class EntryLogger(string category, Action<LogEntry> sink, LogLevel minimumLevel) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel && logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        if (IsEnabled(logLevel))
        {
            sink(new LogEntry(DateTime.Now, logLevel, category, formatter(state, exception), exception));
        }
    }
}
