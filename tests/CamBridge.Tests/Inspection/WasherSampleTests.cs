using CamBridge.Inspection;
using OpenCvSharp;

namespace CamBridge.Tests.Inspection;

/// <summary>
/// The committed real washer set (samples/washer): good washers photographed on a backlight with the
/// acA640-120gm, judged by a recipe whose limits came from twenty other washers. No defective washers
/// have been made yet, so the defect cases are drawn onto a real image.
/// </summary>
public sealed class WasherSampleTests : IDisposable
{
    private const double MillimetersPerPixel = 0.0394; // 16 mm outer diameter at the recorded setup
    private const double SquareMillimetersPerPixel = MillimetersPerPixel * MillimetersPerPixel;
    private const int Backlight = 192;                 // background gray level at the recorded setup
    private const int Threshold = 100;                 // the recipe's fixed threshold
    private const string DefectSource = "ok_01.png";

    private readonly InspectionRecipe _recipe;
    private readonly InspectionEngine _engine;

    public WasherSampleTests()
    {
        string recipePath = Path.Combine(SampleDir, "washer.recipe.json");
        _recipe = RecipeSerializer.Load(recipePath);
        _engine = InspectionEngine.Load(recipePath);
    }

    private static string SampleDir => RepositoryPaths.Sample("washer");

    public void Dispose() => _engine.Dispose();

    [Theory]
    [InlineData(1.00)]
    [InlineData(0.97)]
    [InlineData(1.03)]
    public void GoodWashers_AreOk_WithinThreePercentOfTheRecordedBrightness(double gain)
    {
        string[] files = Directory.GetFiles(Path.Combine(SampleDir, "images"), "ok_*.png").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(6, files.Length);

        var failures = new List<string>();
        foreach (string file in files)
        {
            using Mat image = Load(file);
            using var adjusted = new Mat();
            image.ConvertTo(adjusted, MatType.CV_8UC1, gain); // saturates like a brighter backlight would

            InspectionResult result = _engine.Inspect(adjusted, 0, DateTime.UtcNow);
            if (result.Verdict != Verdict.Ok)
            {
                failures.Add($"{Path.GetFileName(file)}: {string.Join("; ", result.Reasons)}");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void OuterEdgeChip_OfAboutThreeSquareMillimeters_IsNgByRingOnly()
    {
        using Mat image = Load(Path.Combine(SampleDir, "images", DefectSource));
        Blob before = LargestBlob(image, BlobPolarity.Dark, WholeImage(image));

        // A disc of backlight centered on the rightmost point of the outline bites a chip out of the edge.
        var edge = new Point(before.Bounds.Right - 1, (int)Math.Round(before.Center.Y));
        Cv2.Circle(image, edge, 36, Scalar.All(Backlight), -1, LineTypes.Link8);
        Blob after = LargestBlob(image, BlobPolarity.Dark, WholeImage(image));
        double chipMm2 = (before.Area - after.Area) * SquareMillimetersPerPixel;

        InspectionResult result = _engine.Inspect(image, 0, DateTime.UtcNow);

        Assert.InRange(chipMm2, 2.5, 3.5);
        Assert.Equal(Verdict.Ng, result.Verdict);
        Assert.Equal(["Ring"], FailedTools(result));
    }

    [Fact]
    public void HoleEnlargedToFiveMillimeters_IsNgByHole()
    {
        using Mat image = Load(Path.Combine(SampleDir, "images", DefectSource));
        Rect holeRoi = Roi("Hole");
        Blob hole = LargestBlob(image, BlobPolarity.Bright, holeRoi);

        var center = new Point((int)Math.Round(hole.Center.X), (int)Math.Round(hole.Center.Y));
        Cv2.Circle(image, center, (int)Math.Round(2.5 / MillimetersPerPixel), Scalar.All(Backlight), -1, LineTypes.Link8);
        double diameterMm = 2 * Math.Sqrt(LargestBlob(image, BlobPolarity.Bright, holeRoi).Area / Math.PI) * MillimetersPerPixel;

        InspectionResult result = _engine.Inspect(image, 0, DateTime.UtcNow);

        Assert.InRange(diameterMm, 4.9, 5.1);
        Assert.Equal(Verdict.Ng, result.Verdict);
        // The larger hole also takes about 4.5 mm² out of the ring area, beyond its lower limit too.
        Assert.Equal(["Ring", "Hole"], FailedTools(result));
    }

    [Fact]
    public void MissingWasher_IsNgByBothTools()
    {
        using var image = new Mat(480, 640, MatType.CV_8UC1, Scalar.All(Backlight));

        InspectionResult result = _engine.Inspect(image, 0, DateTime.UtcNow);

        Assert.Equal(Verdict.Ng, result.Verdict);
        Assert.Equal(["Ring", "Hole"], FailedTools(result));
    }

    private static Mat Load(string path)
    {
        Mat image = Cv2.ImRead(path, ImreadModes.Grayscale);
        Assert.False(image.Empty(), $"Could not read {path}");
        Assert.Equal(new Size(640, 480), image.Size());
        return image;
    }

    private static Rect WholeImage(Mat image) => new(0, 0, image.Width, image.Height);

    private Rect Roi(string toolName)
    {
        RoiRect roi = _recipe.Tools.Single(t => t.Name == toolName).Roi;
        return new Rect(roi.X, roi.Y, roi.Width, roi.Height);
    }

    private static string[] FailedTools(InspectionResult result) =>
        result.Tools.Where(t => !t.Passed).Select(t => t.ToolName).ToArray();

    /// <summary>The largest connected region on the recipe's side of the threshold, in image coordinates.</summary>
    private static Blob LargestBlob(Mat gray, BlobPolarity polarity, Rect roi)
    {
        using var region = new Mat(gray, roi);
        using var binary = new Mat();
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        Cv2.Threshold(region, binary, Threshold, 255, polarity == BlobPolarity.Dark ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary);
        int count = Cv2.ConnectedComponentsWithStats(binary, labels, stats, centroids, PixelConnectivity.Connectivity8);

        int best = 0;
        for (int label = 1; label < count; label++)
        {
            if (best == 0 || stats.At<int>(label, (int)ConnectedComponentsTypes.Area) > stats.At<int>(best, (int)ConnectedComponentsTypes.Area))
            {
                best = label;
            }
        }

        Assert.NotEqual(0, best);
        return new Blob(
            stats.At<int>(best, (int)ConnectedComponentsTypes.Area),
            new Point2d(centroids.At<double>(best, 0) + roi.X, centroids.At<double>(best, 1) + roi.Y),
            new Rect(
                stats.At<int>(best, (int)ConnectedComponentsTypes.Left) + roi.X,
                stats.At<int>(best, (int)ConnectedComponentsTypes.Top) + roi.Y,
                stats.At<int>(best, (int)ConnectedComponentsTypes.Width),
                stats.At<int>(best, (int)ConnectedComponentsTypes.Height)));
    }

    private readonly record struct Blob(int Area, Point2d Center, Rect Bounds);
}
