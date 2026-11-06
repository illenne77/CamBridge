using CamBridge.Core;
using CamBridge.Inspection;

namespace CamBridge.Tests.Inspection;

public sealed class InspectionConsumerWarmUpTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cambridge-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static InspectionEngine Engine() => InspectionEngine.Create(new InspectionRecipe
    {
        Name = "warm-up",
        Tools = [new BlobToolSettings { Name = "Blob", Roi = new RoiRect(0, 0, 16, 16), Count = new ValueRange(Min: 1) }],
    });

    private static Frame Blank(PixelFormat format)
    {
        Frame frame = Frame.Rent(32, 24, format, 0, DateTime.UtcNow);
        frame.GetWritableData().Clear();
        return frame;
    }

    [Theory]
    [InlineData(PixelFormat.Mono8)]
    [InlineData(PixelFormat.Bgr8)]
    public void WarmUp_LeavesNoTrace(PixelFormat format)
    {
        using InspectionEngine engine = Engine();
        string csvPath = Path.Combine(_dir, "results.csv");
        int raised = 0;
        using (var csv = new CsvResultLogger(csvPath, engine.MeasurementColumns))
        using (var consumer = new InspectionConsumer(engine, csv))
        using (Frame sample = Blank(format))
        {
            consumer.ResultProduced += (_, _) => raised++;

            consumer.WarmUp(sample);

            Assert.Equal(0, consumer.OkCount);
            Assert.Equal(0, consumer.NgCount);
            Assert.Null(consumer.LatestResult);
        }

        Assert.Equal(0, raised);
        Assert.Single(File.ReadAllLines(csvPath)); // header only
    }

    [Fact]
    public async Task WarmUp_TakesUpAPendingRecipe_ThatTheNextFrameUses()
    {
        using InspectionEngine first = Engine();
        using var consumer = new InspectionConsumer(first);
        consumer.ReplaceEngine(InspectionEngine.Create(new InspectionRecipe
        {
            Name = "replacement",
            Tools = [new BlobToolSettings { Name = "Other", Roi = new RoiRect(0, 0, 8, 8) }],
        }));

        using (Frame sample = Blank(PixelFormat.Mono8))
        {
            consumer.WarmUp(sample);
        }

        using (Frame frame = Blank(PixelFormat.Mono8))
        {
            await consumer.ProcessAsync(frame, CancellationToken.None);
        }

        Assert.Equal(["Other"], consumer.LatestResult!.Tools.Select(t => t.ToolName));
    }
}
