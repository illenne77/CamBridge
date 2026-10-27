namespace CamBridge.Pipeline.Saving;

public enum ImageFileFormat
{
    Png,
    Bmp,
    Jpeg,
}

public static class ImageFileFormatExtensions
{
    public static string FileExtension(this ImageFileFormat format) => format switch
    {
        ImageFileFormat.Png => ".png",
        ImageFileFormat.Bmp => ".bmp",
        ImageFileFormat.Jpeg => ".jpg",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown image format."),
    };
}
