using CamBridge.App.Imaging;

namespace CamBridge.Tests.Windows.App;

public sealed class ViewTransformTests
{
    [Fact]
    public void Fit_ScalesToSmallerRatioAndCenters()
    {
        var t = new ViewTransform();

        t.Fit(viewportWidth: 800, viewportHeight: 300, imageWidth: 640, imageHeight: 480);

        Assert.Equal(0.625, t.Scale, precision: 9); // limited by height: 300 / 480
        Assert.Equal((800 - (640 * 0.625)) / 2, t.OffsetX, precision: 9);
        Assert.Equal(0, t.OffsetY, precision: 9);
        Assert.True(t.IsFitted);
    }

    [Fact]
    public void ZoomAt_KeepsImagePointUnderCursor()
    {
        var t = new ViewTransform();
        t.Fit(800, 600, 640, 480);
        (double beforeX, double beforeY) = t.ToImage(300, 200);

        t.ZoomAt(2.5, 300, 200);
        (double afterX, double afterY) = t.ToImage(300, 200);

        Assert.Equal(beforeX, afterX, precision: 9);
        Assert.Equal(beforeY, afterY, precision: 9);
        Assert.False(t.IsFitted);
    }

    [Fact]
    public void ZoomAt_ClampsScale()
    {
        var t = new ViewTransform();

        t.ZoomAt(1e6, 0, 0);
        Assert.Equal(ViewTransform.MaxScale, t.Scale);

        t.ZoomAt(1e-9, 0, 0);
        Assert.Equal(ViewTransform.MinScale, t.Scale);
    }

    [Fact]
    public void ToScreen_InvertsToImage()
    {
        var t = new ViewTransform();
        t.Fit(1000, 700, 640, 480);
        t.ZoomAt(3, 123, 456);
        t.Pan(-40, 25);

        (double sx, double sy) = t.ToScreen(37.5, 211.25);
        (double ix, double iy) = t.ToImage(sx, sy);

        Assert.Equal(37.5, ix, precision: 9);
        Assert.Equal(211.25, iy, precision: 9);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(4.99, 4.99, 0, 0)]
    [InlineData(5, 10, 1, 2)]
    [InlineData(19.99, 9.99, 3, 1)]
    public void PixelAt_FloorsToContainingPixel(double screenX, double screenY, int expectedX, int expectedY)
    {
        var t = new ViewTransform();
        t.ZoomAt(5, 0, 0); // each image pixel covers 5x5 screen units

        Assert.Equal((expectedX, expectedY), t.PixelAt(screenX, screenY, 640, 480));
    }

    [Fact]
    public void PixelAt_OutsideImage_IsNull()
    {
        var t = new ViewTransform();

        Assert.Null(t.PixelAt(-0.5, 10, 640, 480));
        Assert.Null(t.PixelAt(640, 10, 640, 480));
        Assert.Null(t.PixelAt(10, 480, 640, 480));
        Assert.Equal((639, 479), t.PixelAt(639.9, 479.9, 640, 480));
    }

    [Fact]
    public void ActualSize_IsOneToOneCentered()
    {
        var t = new ViewTransform();

        t.ActualSize(1000, 800, 640, 480);

        Assert.Equal(1, t.Scale);
        Assert.Equal(180, t.OffsetX);
        Assert.Equal(160, t.OffsetY);
        Assert.False(t.IsFitted);
    }
}
