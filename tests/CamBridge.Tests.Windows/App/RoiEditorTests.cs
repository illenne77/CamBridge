using CamBridge.App.Imaging;
using CamBridge.Inspection;

namespace CamBridge.Tests.Windows.App;

public sealed class RoiEditorTests
{
    private static readonly RoiRect Roi = new(100, 50, 80, 40); // right 180, bottom 90

    [Theory]
    [InlineData(100, 50, RoiHandle.TopLeft)]
    [InlineData(180, 90, RoiHandle.BottomRight)]
    [InlineData(181.5, 49, RoiHandle.TopRight)]
    [InlineData(140, 50.5, RoiHandle.Top)]
    [InlineData(99, 70, RoiHandle.Left)]
    [InlineData(140, 70, RoiHandle.Body)]
    [InlineData(140, 95, RoiHandle.None)]
    [InlineData(50, 70, RoiHandle.None)]
    public void HitTest_FindsCornersEdgesAndBody(double x, double y, RoiHandle expected)
    {
        Assert.Equal(expected, RoiEditor.HitTest(Roi, x, y, tolerance: 2));
    }

    [Fact]
    public void Drag_Body_MovesAndStaysInsideImage()
    {
        Assert.Equal(new RoiRect(110, 45, 80, 40), RoiEditor.Drag(Roi, RoiHandle.Body, 10.4, -5, 320, 240));
        Assert.Equal(new RoiRect(240, 0, 80, 40), RoiEditor.Drag(Roi, RoiHandle.Body, 500, -500, 320, 240));
    }

    [Fact]
    public void Drag_Corner_ResizesTwoEdges()
    {
        Assert.Equal(new RoiRect(100, 50, 100, 30), RoiEditor.Drag(Roi, RoiHandle.BottomRight, 20, -10, 320, 240));
        Assert.Equal(new RoiRect(90, 40, 90, 50), RoiEditor.Drag(Roi, RoiHandle.TopLeft, -10, -10, 320, 240));
    }

    [Fact]
    public void Drag_Edge_KeepsMinimumSizeAndImageBounds()
    {
        RoiRect collapsed = RoiEditor.Drag(Roi, RoiHandle.Left, 500, 0, 320, 240);
        Assert.Equal(new RoiRect(176, 50, RoiEditor.MinSize, 40), collapsed);

        RoiRect grown = RoiEditor.Drag(Roi, RoiHandle.Right, 500, 0, 320, 240);
        Assert.Equal(new RoiRect(100, 50, 220, 40), grown);
        Assert.True(grown.FitsWithin(320, 240));

        RoiRect up = RoiEditor.Drag(Roi, RoiHandle.Top, 0, -500, 320, 240);
        Assert.Equal(new RoiRect(100, 0, 80, 90), up);
    }

    [Fact]
    public void FromCorners_NormalizesOrderAndCoversTouchedPixels()
    {
        Assert.Equal(new RoiRect(10, 20, 31, 11), RoiEditor.FromCorners(40.2, 30.5, 10.9, 20.1, 320, 240));
    }

    [Fact]
    public void FromCorners_TinyOrOutside_IsClampedToValidRoi()
    {
        RoiRect tiny = RoiEditor.FromCorners(5, 5, 5.2, 5.1, 320, 240);
        Assert.Equal(RoiEditor.MinSize, tiny.Width);
        Assert.Equal(RoiEditor.MinSize, tiny.Height);

        RoiRect outside = RoiEditor.FromCorners(-50, -50, 400, 300, 320, 240);
        Assert.Equal(new RoiRect(0, 0, 320, 240), outside);
    }

    [Fact]
    public void Clamp_MovesRoiFromAnotherResolutionIntoImage()
    {
        // A recipe made for 640x480 used on a 320x240 camera.
        Assert.Equal(new RoiRect(220, 140, 100, 100), RoiEditor.Clamp(new RoiRect(500, 400, 100, 100), 320, 240));
        Assert.Equal(new RoiRect(0, 0, 320, 240), RoiEditor.Clamp(new RoiRect(0, 0, 640, 480), 320, 240));
    }
}
