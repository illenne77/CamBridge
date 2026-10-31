using CamBridge.Core;
using CamBridge.Inspection;
using CamBridge.SampleGenerator;
using CamBridge.Pipeline;
using CamBridge.Tests.Fakes;
using OpenCvSharp;

namespace CamBridge.Tests.Inspection;

/// <summary>
/// Step 5 acceptance: a folder of sample images on disk, a recipe file with a template, and the
/// inspection running as a pipeline consumer yields the expected OK/NG per image plus a CSV log.
/// </summary>
public sealed class SampleFolderInspectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cambridge-tests", Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void WriteSampleFolder()
    {
        string images = Path.Combine(_dir, "images");
        Directory.CreateDirectory(images);
        var samples = new (string Name, Mat Image)[]
        {
            ("01_ok_clean", SampleImages.Create(seed: 11)),
            ("02_ok_spot_outside_roi", SampleImages.Create(defects: [new Point(30, 200)], seed: 12)),
            ("03_ok_tiny_noise", SampleImages.Create(defects: [new Point(200, 120)], defectRadius: 0, seed: 13)),
            ("04_ng_one_spot", SampleImages.Create(defects: [new Point(200, 120)], seed: 14)),
            ("05_ng_two_spots", SampleImages.Create(defects: [new Point(150, 80), new Point(270, 180)], seed: 15)),
            ("06_ng_missing_mark", SampleImages.Create(withFiducial: false, seed: 16)),
        };

        foreach ((string name, Mat image) in samples)
        {
            Cv2.ImWrite(Path.Combine(images, name + ".png"), image);
            image.Dispose();
        }

        using Mat template = SampleImages.CreateTemplate();
        Cv2.ImWrite(Path.Combine(_dir, "fiducial.png"), template);
        RecipeSerializer.Save(SampleImages.Recipe(), Path.Combine(_dir, "part.recipe.json"));
    }

    [Fact]
    public async Task SampleFolder_ProducesExpectedVerdicts_ThroughPipeline()
    {
        WriteSampleFolder();
        using InspectionEngine engine = InspectionEngine.Load(Path.Combine(_dir, "part.recipe.json"));
        string csvPath = Path.Combine(_dir, "results.csv");
        var camera = new FakeCamera(new CameraInfo("fake", "1", "Fake", "Model"));
        var results = new List<InspectionResult>();

        string[] files = Directory.GetFiles(Path.Combine(_dir, "images"), "*.png").Order(StringComparer.Ordinal).ToArray();
        using (var csv = new CsvResultLogger(csvPath, engine.MeasurementColumns))
        {
            var consumer = new InspectionConsumer(engine, csv);
            consumer.ResultProduced += (_, e) => results.Add(e.Result);

            await using var pipeline = new AcquisitionPipeline(camera);
            pipeline.AddConsumer(consumer, new ConsumerOptions { Capacity = files.Length });
            pipeline.Start();

            for (int i = 0; i < files.Length; i++)
            {
                using Mat image = Cv2.ImRead(files[i], ImreadModes.Grayscale);
                camera.Emit(FrameMat.ToFrame(image, i + 1, DateTime.UtcNow));
            }

            await pipeline.StopAsync(drain: true);

            Assert.Equal(3, consumer.OkCount);
            Assert.Equal(3, consumer.NgCount);
            Assert.Equal(files.Length, consumer.LatestResult!.FrameId);
        }

        Assert.Equal(files.Length, results.Count);
        for (int i = 0; i < files.Length; i++)
        {
            Verdict expected = Path.GetFileName(files[i]).Contains("_ok_", StringComparison.Ordinal) ? Verdict.Ok : Verdict.Ng;
            Assert.True(expected == results[i].Verdict, $"{Path.GetFileName(files[i])}: expected {expected}, got {results[i].Verdict} ({string.Join("; ", results[i].Reasons)})");
        }

        string[] csvLines = File.ReadAllLines(csvPath);
        Assert.Equal(files.Length + 1, csvLines.Length);
        Assert.Equal(
            "TimestampUtc,FrameId,Verdict,DurationMs,Reasons,Fiducial.Score,Fiducial.X,Fiducial.Y,Stain.Count,Stain.TotalArea,Stain.MaxArea,Stain.Threshold",
            csvLines[0]);
        Assert.Contains(",NG,", csvLines[6], StringComparison.Ordinal);
        Assert.Contains("Fiducial: Score", csvLines[6], StringComparison.Ordinal);
    }
}
