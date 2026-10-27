using CamBridge.Inspection;
using CamBridge.Inspection.Tools;
using OpenCvSharp;

namespace CamBridge.Tests.Inspection;

public class BlobToolTests
{
    private static BlobToolSettings Settings(Func<BlobToolSettings, BlobToolSettings>? configure = null)
    {
        var settings = new BlobToolSettings
        {
            Name = "Stain",
            Roi = SampleImages.DefectRoi,
            Threshold = 100,
            MinBlobArea = 5,
            Count = new ValueRange(Max: 0),
        };
        return configure?.Invoke(settings) ?? settings;
    }

    [Fact]
    public void CleanSurface_Passes()
    {
        using Mat image = SampleImages.Create();
        using var tool = new BlobTool(Settings());

        ToolResult result = tool.Run(image);

        Assert.True(result.Passed);
        Assert.Empty(result.Reasons);
        Assert.Equal(0, result.Measurements.Single(m => m.Name == BlobTool.Count).Value);
    }

    [Fact]
    public void Spots_AreCountedAndReported()
    {
        using Mat image = SampleImages.Create(defects: [new Point(170, 100), new Point(250, 170)]);
        using var tool = new BlobTool(Settings());

        ToolResult result = tool.Run(image);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Measurements.Single(m => m.Name == BlobTool.Count).Value);
        Assert.Equal("Stain: Count 2 > max 0", Assert.Single(result.Reasons));

        // Blob boxes are reported in image coordinates, not ROI coordinates.
        RoiRect[] blobs = result.Overlays.Where(o => o.Kind == OverlayKind.Blob).Select(o => o.Rect).ToArray();
        Assert.Equal(2, blobs.Length);
        Assert.Contains(blobs, b => b.X <= 170 && b.Right > 170 && b.Y <= 100 && b.Bottom > 100);
    }

    [Fact]
    public void SpotsOutsideRoi_AreIgnored()
    {
        using Mat image = SampleImages.Create(defects: [new Point(20, 200)]);
        using var tool = new BlobTool(Settings());

        Assert.True(tool.Run(image).Passed);
    }

    [Fact]
    public void MinBlobArea_FiltersNoise()
    {
        using Mat image = SampleImages.Create(defects: [new Point(170, 100)], defectRadius: 1);
        using var tool = new BlobTool(Settings(s => s with { MinBlobArea = 20 }));

        Assert.True(tool.Run(image).Passed);
    }

    [Fact]
    public void AreaLimits_AreJudged()
    {
        using Mat image = SampleImages.Create(defects: [new Point(170, 100)], defectRadius: 6);
        using var tool = new BlobTool(Settings(s => s with { Count = null, MaxArea = new ValueRange(Max: 50) }));

        ToolResult result = tool.Run(image);

        Assert.False(result.Passed);
        Assert.StartsWith("Stain: MaxArea ", Assert.Single(result.Reasons), StringComparison.Ordinal);
    }

    [Fact]
    public void Otsu_FindsSpotsWithoutFixedThreshold()
    {
        using Mat image = SampleImages.Create(defects: [new Point(170, 100)]);
        using var tool = new BlobTool(Settings(s => s with { ThresholdMode = ThresholdMode.Otsu, Threshold = 0 }));

        ToolResult result = tool.Run(image);

        Assert.Equal(1, result.Measurements.Single(m => m.Name == BlobTool.Count).Value);
        double threshold = result.Measurements.Single(m => m.Name == BlobTool.ThresholdUsed).Value;
        Assert.InRange(threshold, 40, 190);
    }

    [Fact]
    public void BrightPolarity_FindsBrightSpots()
    {
        using Mat image = SampleImages.Create();
        Cv2.Circle(image, new Point(200, 120), 5, Scalar.All(255), -1);
        using var tool = new BlobTool(Settings(s => s with { Polarity = BlobPolarity.Bright, Threshold = 230 }));

        ToolResult result = tool.Run(image);

        Assert.Equal(1, result.Measurements.Single(m => m.Name == BlobTool.Count).Value);
    }

    [Fact]
    public void RoiOutsideImage_IsConfigurationErrorNg()
    {
        using Mat image = SampleImages.Create();
        using var tool = new BlobTool(Settings(s => s with { Roi = new RoiRect(300, 200, 50, 50) }));

        ToolResult result = tool.Run(image);

        Assert.False(result.Passed);
        Assert.True(result.IsConfigurationError);
        Assert.Contains("configuration error", Assert.Single(result.Reasons), StringComparison.Ordinal);
    }
}
