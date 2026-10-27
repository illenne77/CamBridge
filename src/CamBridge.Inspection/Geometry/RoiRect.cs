namespace CamBridge.Inspection;

/// <summary>Axis-aligned rectangle in image pixel coordinates.</summary>
public readonly record struct RoiRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    /// <summary>True when the rectangle is non-empty and lies completely inside an image of the given size.</summary>
    public bool FitsWithin(int imageWidth, int imageHeight) =>
        Width > 0 && Height > 0 && X >= 0 && Y >= 0 && Right <= imageWidth && Bottom <= imageHeight;

    public RoiRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    public override string ToString() => $"({X},{Y},{Width}x{Height})";
}
