using CamBridge.Core;

namespace CamBridge.Sources.ImageFolder;

/// <summary>
/// Exposes each configured folder that contains images as a virtual camera.
/// </summary>
public sealed class ImageFolderProvider : ICameraProvider
{
    public const string Id = "imagefolder";

    internal static readonly string[] SupportedExtensions = [".png", ".bmp", ".jpg", ".jpeg", ".tif", ".tiff"];

    private readonly string[] _folders;
    private readonly ImageFolderOptions _options;

    public ImageFolderProvider(IEnumerable<string> folders, ImageFolderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(folders);

        _folders = folders.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _options = options ?? new ImageFolderOptions();
    }

    public string ProviderId => Id;

    public string VendorName => "Image Folder";

    public Task<IReadOnlyList<CameraInfo>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        var cameras = new List<CameraInfo>();
        foreach (string folder in _folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(folder) && EnumerateImages(folder).Any())
            {
                cameras.Add(CreateInfo(folder));
            }
        }

        return Task.FromResult<IReadOnlyList<CameraInfo>>(cameras);
    }

    public ICamera Create(CameraInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!string.Equals(info.ProviderId, Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Camera '{info.DeviceId}' belongs to provider '{info.ProviderId}'.", nameof(info));
        }

        return new ImageFolderCamera(info, _options);
    }

    /// <summary>Describes a folder as a camera; its full path is the device id.</summary>
    public static CameraInfo CreateInfo(string folder)
    {
        string fullPath = Path.GetFullPath(folder);
        return new CameraInfo(Id, fullPath, "CamBridge", "Image Folder")
        {
            UserDefinedName = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath)),
            Interface = CameraInterface.File,
        };
    }

    /// <summary>Supported image files in ordinal name order (playback order).</summary>
    internal static IEnumerable<string> EnumerateImages(string folder) =>
        Directory.EnumerateFiles(folder)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);
}
