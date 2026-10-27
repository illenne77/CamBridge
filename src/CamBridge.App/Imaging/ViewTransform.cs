namespace CamBridge.App.Imaging;

/// <summary>
/// Maps between viewport (screen) and image pixel coordinates for zoom and pan:
/// screen = image * Scale + Offset. Kept free of WPF types so it can be unit tested.
/// </summary>
public sealed class ViewTransform
{
    public const double MinScale = 0.05;
    public const double MaxScale = 64;

    public double Scale { get; private set; } = 1;

    public double OffsetX { get; private set; }

    public double OffsetY { get; private set; }

    /// <summary>True until the user zooms or pans; the view then refits on resize.</summary>
    public bool IsFitted { get; private set; } = true;

    /// <summary>Scales the image to fit the viewport and centers it.</summary>
    public void Fit(double viewportWidth, double viewportHeight, int imageWidth, int imageHeight)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0 || imageWidth <= 0 || imageHeight <= 0)
        {
            return;
        }

        Scale = Math.Clamp(Math.Min(viewportWidth / imageWidth, viewportHeight / imageHeight), MinScale, MaxScale);
        Center(viewportWidth, viewportHeight, imageWidth, imageHeight);
        IsFitted = true;
    }

    /// <summary>Shows image pixels 1:1, centered.</summary>
    public void ActualSize(double viewportWidth, double viewportHeight, int imageWidth, int imageHeight)
    {
        Scale = 1;
        Center(viewportWidth, viewportHeight, imageWidth, imageHeight);
        IsFitted = false;
    }

    /// <summary>Multiplies the scale by <paramref name="factor"/>, keeping the image point under the cursor fixed.</summary>
    public void ZoomAt(double factor, double screenX, double screenY)
    {
        double newScale = Math.Clamp(Scale * factor, MinScale, MaxScale);
        (double imageX, double imageY) = ToImage(screenX, screenY);
        Scale = newScale;
        OffsetX = screenX - (imageX * newScale);
        OffsetY = screenY - (imageY * newScale);
        IsFitted = false;
    }

    public void Pan(double dx, double dy)
    {
        OffsetX += dx;
        OffsetY += dy;
        IsFitted = false;
    }

    public (double X, double Y) ToImage(double screenX, double screenY) =>
        ((screenX - OffsetX) / Scale, (screenY - OffsetY) / Scale);

    public (double X, double Y) ToScreen(double imageX, double imageY) =>
        ((imageX * Scale) + OffsetX, (imageY * Scale) + OffsetY);

    /// <summary>Pixel under a screen point, or null when it falls outside the image.</summary>
    public (int X, int Y)? PixelAt(double screenX, double screenY, int imageWidth, int imageHeight)
    {
        (double x, double y) = ToImage(screenX, screenY);
        int px = (int)Math.Floor(x);
        int py = (int)Math.Floor(y);
        return px >= 0 && py >= 0 && px < imageWidth && py < imageHeight ? (px, py) : null;
    }

    private void Center(double viewportWidth, double viewportHeight, int imageWidth, int imageHeight)
    {
        OffsetX = (viewportWidth - (imageWidth * Scale)) / 2;
        OffsetY = (viewportHeight - (imageHeight * Scale)) / 2;
    }
}
