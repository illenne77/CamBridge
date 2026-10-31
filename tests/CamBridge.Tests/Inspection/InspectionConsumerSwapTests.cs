using CamBridge.Core;
using CamBridge.Inspection;
using OpenCvSharp;

namespace CamBridge.Tests.Inspection;

public sealed class InspectionConsumerSwapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cambridge-tests", Guid.NewGuid().ToString("N"));

    public InspectionConsumerSwapTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A blob tool over a uniform image: Count is 0, so the limit decides the verdict.</summary>
    private static InspectionEngine Engine(bool passes) => InspectionEngine.Create(new InspectionRecipe
    {
        Name = passes ? "pass" : "fail",
        Tools =
        [
            new BlobToolSettings
            {
                Name = "Blob",
                Roi = new RoiRect(0, 0, 16, 16),
                Count = passes ? new ValueRange(Max: 10) : new ValueRange(Min: 1),
            },
        ],
    });

    private static Frame NewFrame(long id)
    {
        using var image = new Mat(16, 16, MatType.CV_8UC1, Scalar.All(200));
        return FrameMat.ToFrame(image, id, DateTime.UtcNow);
    }

    private static async Task<Verdict> InspectAsync(InspectionConsumer consumer, long id)
    {
        using Frame frame = NewFrame(id);
        await consumer.ProcessAsync(frame, CancellationToken.None);
        return consumer.LatestResult!.Verdict;
    }

    private CsvResultLogger Csv(string name, InspectionEngine engine) => new(Path.Combine(_dir, name), engine.MeasurementColumns);

    /// <summary>True once the logger that wrote <paramref name="name"/> has closed its file.</summary>
    private bool IsClosed(string name)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(_dir, name), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    [Fact]
    public async Task ReplaceEngine_TakesEffectFromNextFrame()
    {
        using InspectionEngine initial = Engine(passes: true);
        using var consumer = new InspectionConsumer(initial);

        Assert.Equal(Verdict.Ok, await InspectAsync(consumer, 1));
        consumer.ReplaceEngine(Engine(passes: false));
        Assert.Equal(Verdict.Ng, await InspectAsync(consumer, 2));

        Assert.Equal(1, consumer.OkCount);
        Assert.Equal(1, consumer.NgCount);
    }

    [Fact]
    public async Task ReplaceEngine_ReleasesOwnedSetupOnlyAfterSwap_AndNeverTheCallersOwn()
    {
        using InspectionEngine initial = Engine(passes: true);
        using CsvResultLogger callerCsv = Csv("caller.csv", initial);
        using var consumer = new InspectionConsumer(initial, callerCsv);

        InspectionEngine first = Engine(passes: false);
        consumer.ReplaceEngine(first, Csv("first.csv", first));
        await InspectAsync(consumer, 1);
        Assert.False(IsClosed("first.csv")); // in use

        InspectionEngine second = Engine(passes: true);
        consumer.ReplaceEngine(second, Csv("second.csv", second));
        Assert.False(IsClosed("first.csv")); // still current until the next frame
        await InspectAsync(consumer, 2);

        Assert.True(IsClosed("first.csv"));
        Assert.False(IsClosed("second.csv"));
        Assert.False(IsClosed("caller.csv")); // constructor logger stays with the caller
        Assert.Equal(1, DataRows("first.csv"));
        Assert.Equal(1, DataRows("second.csv"));
    }

    /// <summary>Rows below the header; reads while the logger may still hold the file open.</summary>
    private int DataRows(string name)
    {
        using var stream = new FileStream(Path.Combine(_dir, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1;
    }

    [Fact]
    public async Task ReplaceEngine_Twice_BeforeAFrame_UsesLastAndReleasesSkipped()
    {
        using InspectionEngine initial = Engine(passes: true);
        using var consumer = new InspectionConsumer(initial);

        InspectionEngine skipped = Engine(passes: true);
        consumer.ReplaceEngine(skipped, Csv("skipped.csv", skipped));
        consumer.ReplaceEngine(Engine(passes: false));

        Assert.True(IsClosed("skipped.csv"));
        Assert.Equal(Verdict.Ng, await InspectAsync(consumer, 1));
    }

    [Fact]
    public async Task Dispose_ReleasesCurrentAndPending_ThenRejectsReplacements()
    {
        using InspectionEngine initial = Engine(passes: true);
        var consumer = new InspectionConsumer(initial);
        InspectionEngine current = Engine(passes: true);
        consumer.ReplaceEngine(current, Csv("current.csv", current));
        await InspectAsync(consumer, 1);
        InspectionEngine pending = Engine(passes: true);
        consumer.ReplaceEngine(pending, Csv("pending.csv", pending));

        consumer.Dispose();

        Assert.True(IsClosed("current.csv"));
        Assert.True(IsClosed("pending.csv"));
        Assert.Throws<ObjectDisposedException>(() => consumer.ReplaceEngine(Engine(passes: true)));
        consumer.Dispose(); // idempotent
    }

    [Fact]
    public async Task ReplaceEngine_FromAnotherThreadWhileInspecting_IsSafe()
    {
        using InspectionEngine initial = Engine(passes: true);
        using var consumer = new InspectionConsumer(initial);
        const int Frames = 300;

        Task swapper = Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                consumer.ReplaceEngine(Engine(passes: i % 2 == 0));
            }
        });

        for (int id = 1; id <= Frames; id++)
        {
            await InspectAsync(consumer, id);
        }

        await swapper;
        Assert.Equal(Frames, consumer.OkCount + consumer.NgCount);
    }
}
