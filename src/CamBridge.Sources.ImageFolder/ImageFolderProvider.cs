using CamBridge.Core;

namespace CamBridge.Sources.ImageFolder;

/// <summary>
/// Exposes each configured folder that contains images as a virtual camera.
/// </summary>
public sealed class ImageFolderProvider : ICameraProvider
{
    public const string Id = "imagefolder";

    internal static readonly string[] SupportedExtensions = [".png", ".bmp", ".jpg", ".jpeg", ".tif", ".tiff"];

    private readonly object _addSync = new();
    private readonly ImageFolderOptions _options;

    // Replaced, never changed in place: enumeration on the thread pool reads a stable snapshot.
    private volatile string[] _folders;

    public ImageFolderProvider(IEnumerable<string> folders, ImageFolderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(folders);

        _folders = folders.Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _options = options ?? new ImageFolderOptions();
    }

    public string ProviderId => Id;

    public string VendorName => "Image Folder";

    /// <summary>
    /// Adds a folder at run time and describes it as a camera. A folder already known keeps its entry.
    /// Unlike the constructor list this is a user choice, so a folder that cannot play is an error
    /// instead of being left out of the enumeration.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    /// <exception cref="InvalidDataException">The folder has no supported images.</exception>
    public CameraInfo AddFolder(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        string path = NormalizePath(folder);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Image folder not found: {path}");
        }

        if (!EnumerateImages(path).Any())
        {
            throw new InvalidDataException($"No supported images in {path}.");
        }

        lock (_addSync)
        {
            string[] folders = _folders;
            string? known = folders.FirstOrDefault(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
            if (known is not null)
            {
                return CreateInfo(known);
            }

            _folders = [.. folders, path];
        }

        return CreateInfo(path);
    }

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
        string fullPath = NormalizePath(folder);
        string name = Path.GetFileName(fullPath);
        return new CameraInfo(Id, fullPath, "CamBridge", "Image Folder")
        {
            UserDefinedName = name.Length > 0 ? name : fullPath, // a drive root has no folder name
            Interface = CameraInterface.File,
        };
    }

    /// <summary>Supported image files in ordinal name order (playback order).</summary>
    internal static IEnumerable<string> EnumerateImages(string folder) =>
        Directory.EnumerateFiles(folder)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);

    private static string NormalizePath(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
}
