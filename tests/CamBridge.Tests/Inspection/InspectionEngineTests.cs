using CamBridge.Core;
using CamBridge.Inspection;
using CamBridge.SampleGenerator;
using OpenCvSharp;

namespace CamBridge.Tests.Inspection;

public sealed class InspectionEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cambridge-tests", Guid.NewGuid().ToString("N"));

    public InspectionEngineTests()
    {
        Directory.CreateDirectory(_dir);
        using Mat template = SampleImages.CreateTemplate();
        Cv2.ImWrite(Path.Combine(_dir, "fiducial.png"), template);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Inspect_GoodPart_IsOk()
    {
        using var engine = InspectionEngine.Create(SampleImages.Recipe(), _dir);
        using Mat image = SampleImages.Create();
        using Frame frame = FrameMat.ToFrame(image, 3, DateTime.UtcNow);

        InspectionResult result = engine.Inspect(frame);

        Assert.Equal(Verdict.Ok, result.Verdict);
        Assert.Equal(3, result.FrameId);
        Assert.Equal(["Fiducial", "Stain"], result.Tools.Select(t => t.ToolName));
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void Inspect_CollectsReasonsFromEveryFailingTool()
    {
        using var engine = InspectionEngine.Create(SampleImages.Recipe(), _dir);
        using Mat image = SampleImages.Create(withFiducial: false, defects: [new Point(200, 120)]);

        InspectionResult result = engine.Inspect(image, 1, DateTime.UtcNow);

        Assert.Equal(Verdict.Ng, result.Verdict);
        Assert.Equal(2, result.Reasons.Count());
    }

    [Fact]
    public void Inspect_ColorFrame_IsConvertedToGray()
    {
        using var engine = InspectionEngine.Create(SampleImages.Recipe(), _dir);
        using Mat gray = SampleImages.Create(defects: [new Point(200, 120)]);
        using var bgr = new Mat();
        Cv2.CvtColor(gray, bgr, ColorConversionCodes.GRAY2BGR);
        using Frame frame = FrameMat.ToFrame(bgr, 1, DateTime.UtcNow);

        InspectionResult result = engine.Inspect(frame);

        Assert.Equal(PixelFormat.Bgr8, frame.PixelFormat);
        Assert.Equal(Verdict.Ng, result.Verdict);
        Assert.True(result.Tools.Single(t => t.ToolName == "Fiducial").Passed);
    }

    [Fact]
    public void DisabledTools_AreSkipped()
    {
        InspectionRecipe recipe = SampleImages.Recipe();
        recipe = recipe with { Tools = [recipe.Tools[0], recipe.Tools[1] with { Enabled = false }] };
        using var engine = InspectionEngine.Create(recipe, _dir);
        using Mat image = SampleImages.Create(defects: [new Point(200, 120)]);

        InspectionResult result = engine.Inspect(image, 1, DateTime.UtcNow);

        Assert.Equal(Verdict.Ok, result.Verdict);
        Assert.Equal(["Fiducial.Score", "Fiducial.X", "Fiducial.Y"], engine.MeasurementColumns);
    }

    [Fact]
    public void Load_ResolvesTemplateRelativeToRecipeFile()
    {
        string recipePath = Path.Combine(_dir, "part.recipe.json");
        RecipeSerializer.Save(SampleImages.Recipe(), recipePath);

        using InspectionEngine engine = InspectionEngine.Load(recipePath);
        using Mat image = SampleImages.Create();

        Assert.Equal(Verdict.Ok, engine.Inspect(image, 1, DateTime.UtcNow).Verdict);
    }

    [Fact]
    public void Create_MissingTemplate_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => InspectionEngine.Create(SampleImages.Recipe("missing.png"), _dir));
    }

    [Fact]
    public void Create_TemplateInFolderOutsideAnsiCodePage_Loads()
    {
        string folder = Path.Combine(_dir, "tiếng-việt-😀");
        Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(_dir, "fiducial.png"), Path.Combine(folder, "fiducial.png"));

        using var engine = InspectionEngine.Create(SampleImages.Recipe(), folder);
        using Mat image = SampleImages.Create();

        Assert.Equal(Verdict.Ok, engine.Inspect(image, 1, DateTime.UtcNow).Verdict);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3, 4 })]
    public void Create_UndecodableTemplate_Throws(byte[] content)
    {
        File.WriteAllBytes(Path.Combine(_dir, "broken.png"), content);

        Assert.Throws<InvalidDataException>(() => InspectionEngine.Create(SampleImages.Recipe("broken.png"), _dir));
    }
}
