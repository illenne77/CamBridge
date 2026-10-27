using CamBridge.Inspection;

namespace CamBridge.Tests.Inspection;

public sealed class CsvResultLoggerTests : IDisposable
{
    private static readonly DateTime Timestamp = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    private readonly string _path = Path.Combine(Path.GetTempPath(), "cambridge-tests", Guid.NewGuid().ToString("N"), "results.csv");

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true);

    private static InspectionResult Result(long frameId, Verdict verdict, params string[] reasons) => new(
        frameId,
        Timestamp,
        verdict,
        [new ToolResult("Stain", verdict == Verdict.Ok, [new Measurement("Count", reasons.Length)], reasons, [], TimeSpan.Zero)],
        TimeSpan.FromMilliseconds(1.5));

    [Fact]
    public void Writes_HeaderOnce_AndEscapesFields()
    {
        string[] columns = ["Stain.Count", "Fiducial.Score"];
        using (var logger = new CsvResultLogger(_path, columns))
        {
            logger.Write(Result(1, Verdict.Ok));
            logger.Write(Result(2, Verdict.Ng, "Stain: Count 2 > max 0", "has \"quote\", comma"));
        }

        using (var logger = new CsvResultLogger(_path, columns))
        {
            logger.Write(Result(3, Verdict.Ok));
        }

        string[] lines = File.ReadAllLines(_path);
        Assert.Equal(
            [
                "TimestampUtc,FrameId,Verdict,DurationMs,Reasons,Stain.Count,Fiducial.Score",
                "2026-03-04T05:06:07.0000000Z,1,OK,1.5,,0,",
                "2026-03-04T05:06:07.0000000Z,2,NG,1.5,\"Stain: Count 2 > max 0; has \"\"quote\"\", comma\",2,",
                "2026-03-04T05:06:07.0000000Z,3,OK,1.5,,0,",
            ],
            lines);
    }

    [Fact]
    public void ExistingFileWithDifferentColumns_Throws()
    {
        using (new CsvResultLogger(_path, ["A.X"]))
        {
        }

        Assert.Throws<InvalidOperationException>(() => new CsvResultLogger(_path, ["B.Y"]));
    }
}
