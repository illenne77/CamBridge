namespace CamBridge.Pipeline.Saving;

public sealed record ImageSaveOptions
{
    /// <summary>Root directory. Saved files never leave it.</summary>
    public required string Directory { get; init; }

    /// <summary>
    /// Relative path without extension. Tokens: {camera}, {frameId}, {timestamp} (local time),
    /// {utc}, {width}, {height}; each accepts a .NET format, e.g. {timestamp:yyyyMMdd} or {frameId:D8}.
    /// </summary>
    public string FileNamePattern { get; init; } = "{camera}/{timestamp:yyyyMMdd}/{timestamp:HHmmss_fff}_{frameId:D8}";

    /// <summary>Value substituted for {camera}.</summary>
    public string CameraName { get; init; } = "camera";

    public ImageFileFormat Format { get; init; } = ImageFileFormat.Png;

    public int JpegQuality { get; init; } = 90;

    /// <summary>Save one of every N frames that reach the consumer (1 = all).</summary>
    public int SaveEveryNth { get; init; } = 1;
}
