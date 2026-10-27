using System.Globalization;
using System.Text;

namespace CamBridge.Inspection;

/// <summary>
/// Appends one CSV row per inspection: time, frame id, verdict, duration, reasons, then one
/// column per "Tool.Measurement". Each row is flushed so a crash loses at most the current row.
/// </summary>
public sealed class CsvResultLogger : IDisposable
{
    private static readonly string[] FixedColumns = ["TimestampUtc", "FrameId", "Verdict", "DurationMs", "Reasons"];

    private readonly IReadOnlyList<string> _measurementColumns;
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    /// <summary>
    /// Opens (or creates) <paramref name="path"/> for appending. An existing file must have the
    /// same header; a different recipe should log to a different file.
    /// </summary>
    public CsvResultLogger(string path, IReadOnlyList<string> measurementColumns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(measurementColumns);

        _measurementColumns = measurementColumns;
        string header = string.Join(',', FixedColumns.Concat(measurementColumns).Select(Escape));

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        bool hasContent = File.Exists(path) && new FileInfo(path).Length > 0;
        if (hasContent)
        {
            string? existing = File.ReadLines(path).FirstOrDefault();
            if (!string.Equals(existing, header, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"'{path}' has a different column layout. Log this recipe to a new file.");
            }
        }

        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        if (!hasContent)
        {
            _writer.WriteLine(header);
            _writer.Flush();
        }
    }

    public void Write(InspectionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (ToolResult tool in result.Tools)
        {
            foreach (Measurement m in tool.Measurements)
            {
                values[$"{tool.ToolName}.{m.Name}"] = m.Value;
            }
        }

        IEnumerable<string> fields = new[]
        {
            result.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
            result.FrameId.ToString(CultureInfo.InvariantCulture),
            result.Verdict == Verdict.Ok ? "OK" : "NG",
            result.Duration.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture),
            string.Join("; ", result.Reasons),
        }.Concat(_measurementColumns.Select(c =>
            values.TryGetValue(c, out double v) ? v.ToString("R", CultureInfo.InvariantCulture) : string.Empty));

        string line = string.Join(',', fields.Select(Escape));
        lock (_lock)
        {
            _writer.WriteLine(line);
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer.Dispose();
        }
    }

    private static string Escape(string field) =>
        field.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? $"\"{field.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : field;
}
