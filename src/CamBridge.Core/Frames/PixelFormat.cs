namespace CamBridge.Core;

/// <summary>
/// Pixel layout of a <see cref="Frame"/> buffer. Kept intentionally small; packed and
/// high bit-depth formats are added when a real camera requires them.
/// </summary>
public enum PixelFormat
{
    /// <summary>8-bit monochrome, 1 byte per pixel.</summary>
    Mono8,

    /// <summary>8-bit per channel color in B, G, R order, 3 bytes per pixel.</summary>
    Bgr8,
}

public static class PixelFormatExtensions
{
    public static int BytesPerPixel(this PixelFormat format) => format switch
    {
        PixelFormat.Mono8 => 1,
        PixelFormat.Bgr8 => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown pixel format."),
    };
}
