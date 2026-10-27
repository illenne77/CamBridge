using CamBridge.Inspection;
using CamBridge.Inspection.Tools;
using OpenCvSharp;

namespace CamBridge.Tests.Inspection;

public class TemplateMatchToolTests
{
    private static TemplateMatchTool Tool(RoiRect? roi = null) => new(
        new TemplateMatchToolSettings
        {
            Name = "Fiducial",
            Roi = roi ?? SampleImages.FiducialSearchRoi,
            TemplatePath = "unused.png",
            MinScore = 0.8,
        },
        SampleImages.CreateTemplate());

    [Fact]
    public void Fiducial_IsFoundAtItsLocation()
    {
        using Mat image = SampleImages.Create(seed: 7); // different noise than the template source
        using TemplateMatchTool tool = Tool();

        ToolResult result = tool.Run(image);

        Assert.True(result.Passed);
        Assert.True(result.Measurements.Single(m => m.Name == TemplateMatchTool.Score).Value > 0.9);
        Assert.Equal(SampleImages.FiducialAt.X, result.Measurements.Single(m => m.Name == TemplateMatchTool.X).Value);
        Assert.Equal(SampleImages.FiducialAt.Y, result.Measurements.Single(m => m.Name == TemplateMatchTool.Y).Value);
        Assert.Equal(SampleImages.FiducialAt, result.Overlays.Single(o => o.Kind == OverlayKind.Match).Rect);
    }

    [Fact]
    public void MissingFiducial_FailsOnScore()
    {
        using Mat image = SampleImages.Create(withFiducial: false);
        using TemplateMatchTool tool = Tool();

        ToolResult result = tool.Run(image);

        Assert.False(result.Passed);
        Assert.StartsWith("Fiducial: Score ", Assert.Single(result.Reasons), StringComparison.Ordinal);
    }

    [Fact]
    public void RoiSmallerThanTemplate_IsConfigurationErrorNg()
    {
        using Mat image = SampleImages.Create();
        using TemplateMatchTool tool = Tool(new RoiRect(40, 40, 20, 20));

        ToolResult result = tool.Run(image);

        Assert.False(result.Passed);
        Assert.True(result.IsConfigurationError);
    }

    [Fact]
    public void RoiOutsideImage_IsConfigurationErrorNg()
    {
        using Mat image = SampleImages.Create();
        using TemplateMatchTool tool = Tool(new RoiRect(-5, 0, 100, 100));

        Assert.True(tool.Run(image).IsConfigurationError);
    }
}
