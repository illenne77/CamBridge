using System.Globalization;
using System.Text.RegularExpressions;
using CamBridge.Core;

namespace CamBridge.Pipeline.Saving;

/// <summary>
/// Saves frames to disk from its own pipeline queue, so slow storage never stalls acquisition
/// or other consumers; when it falls behind, its queue policy drops frames and counts them.
/// Frames skipped by <see cref="ImageSaveOptions.SaveEveryNth"/> never enter the queue, so a
/// dropped frame always means a save that did not happen.
/// </summary>
public sealed partial class ImageSaveConsumer : IFrameConsumer
{
    private readonly ImageSaveOptions _options;
    private readonly IFrameEncoder _encoder;
    private readonly string _root;
    private long _seen;

    public ImageSaveConsumer(ImageSaveOptions options, IFrameEncoder? encoder = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Directory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.SaveEveryNth);

        _options = options;
        _encoder = encoder ?? new OpenCvFrameEncoder();
        _root = Path.GetFullPath(options.Directory);
    }

    public string Name => "ImageSave";

    /// <summary>Path of the most recently saved file.</summary>
    public string? LastSavedPath { get; private set; }

    public bool Accepts(Frame frame) => (Interlocked.Increment(ref _seen) - 1) % _options.SaveEveryNth == 0;

    public async ValueTask ProcessAsync(Frame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);

        string path = BuildPath(frame);
        byte[] payload = _encoder.Encode(frame, _options.Format, _options.JpegQuality);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, payload, cancellationToken).ConfigureAwait(false);
        LastSavedPath = path;
    }

    /// <summary>Resolves the file path for a frame. Throws if the pattern escapes the root directory.</summary>
    public string BuildPath(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        string relative = TokenRegex().Replace(_options.FileNamePattern, match =>
        {
            string name = match.Groups["name"].Value;
            string? format = match.Groups["format"].Success ? match.Groups["format"].Value : null;
            object value = name.ToUpperInvariant() switch
            {
                "CAMERA" => _options.CameraName,
                "FRAMEID" => frame.FrameId,
                "TIMESTAMP" => frame.TimestampUtc.ToLocalTime(),
                "UTC" => frame.TimestampUtc,
                "WIDTH" => frame.Width,
                "HEIGHT" => frame.Height,
                _ => throw new FormatException($"Unknown token '{{{name}}}' in file name pattern."),
            };

            return value is IFormattable formattable
                ? formattable.ToString(format, CultureInfo.InvariantCulture)
                : value.ToString() ?? string.Empty;
        });

        string full = Path.GetFullPath(Path.Combine(_root, relative + _options.Format.FileExtension()));
        string rootWithSeparator = Path.EndsInDirectorySeparator(_root) ? _root : _root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"File name pattern resolves outside '{_root}': {full}");
        }

        return full;
    }

    [GeneratedRegex(@"\{(?<name>[A-Za-z]+)(:(?<format>[^}]+))?\}")]
    private static partial Regex TokenRegex();
}
