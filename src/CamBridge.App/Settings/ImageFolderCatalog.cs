using System.IO;
using System.Text.Json;
using CamBridge.Core;
using CamBridge.Sources.ImageFolder;
using Microsoft.Extensions.Logging;

namespace CamBridge.App.Settings;

/// <summary>Image folders shown as cameras that the user can add and remove at run time.</summary>
public interface IImageFolderCatalog
{
    /// <summary>Adds a folder (or returns the known entry) and remembers it for the next run.</summary>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    /// <exception cref="InvalidDataException">The folder has no supported images.</exception>
    CameraInfo Add(string folder);

    /// <summary>
    /// True for folders the user added, in this run or an earlier one; their device id is the folder
    /// path. The bundled sample is not one: it comes back on every run, so it cannot be removed.
    /// </summary>
    bool IsAddedFolder(CameraInfo camera);

    /// <summary>Removes an added folder from the list and from the saved settings. Files are not touched.</summary>
    void Remove(CameraInfo camera);
}

/// <summary>
/// The bundled sample folder plus the folders the user added, restored from
/// <see cref="UserSettingsStore"/> at startup.
/// </summary>
/// <remarks>
/// Settings are read again before each save, so two running instances do not drop each other's
/// folders. A saved folder that is missing (unplugged drive, offline share) stays saved: the
/// enumeration skips it and a refresh brings it back once it is reachable. Failing to save only
/// logs a warning, because the folder is already usable in this run.
/// </remarks>
public sealed partial class ImageFolderCatalog : IImageFolderCatalog
{
    private readonly ImageFolderProvider _provider;
    private readonly UserSettingsStore _store;
    private readonly string? _sampleId;
    private readonly ILogger _logger;
    private readonly object _saveSync = new();

    public ImageFolderCatalog(string? sampleFolder, UserSettingsStore store, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _logger = logger;
        _sampleId = sampleFolder is null ? null : ImageFolderProvider.CreateInfo(sampleFolder).DeviceId;
        IReadOnlyList<string> saved = ReadSettings().ImageFolders;
        _provider = new ImageFolderProvider(sampleFolder is null ? saved : [sampleFolder, .. saved]);
    }

    public ICameraProvider Provider => _provider;

    public CameraInfo Add(string folder)
    {
        CameraInfo camera = _provider.AddFolder(folder); // throws before anything is saved
        if (IsAddedFolder(camera))
        {
            Save(folders => folders.Any(f => IsSameFolder(f, camera.DeviceId)) ? null : [.. folders, camera.DeviceId]);
        }

        return camera;
    }

    public bool IsAddedFolder(CameraInfo camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        return string.Equals(camera.ProviderId, ImageFolderProvider.Id, StringComparison.OrdinalIgnoreCase)
            && !IsSameFolder(camera.DeviceId, _sampleId);
    }

    public void Remove(CameraInfo camera)
    {
        if (!IsAddedFolder(camera))
        {
            throw new InvalidOperationException($"'{camera.DeviceId}' is not a folder the user added.");
        }

        _provider.RemoveFolder(camera.DeviceId);
        Save(folders => folders.Any(f => IsSameFolder(f, camera.DeviceId))
            ? folders.Where(f => !IsSameFolder(f, camera.DeviceId)).ToList()
            : null);
    }

    private static bool IsSameFolder(string a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <param name="change">Returns the new list, or null when nothing changes.</param>
    private void Save(Func<IReadOnlyList<string>, IReadOnlyList<string>?> change)
    {
        lock (_saveSync)
        {
            UserSettings settings = ReadSettings();
            if (change(settings.ImageFolders) is not { } folders)
            {
                return;
            }

            try
            {
                _store.Save(settings with { ImageFolders = folders });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogSaveFailed(_logger, ex, _store.FilePath);
            }
        }
    }

    /// <summary>An unreadable file counts as empty; the next save replaces it.</summary>
    private UserSettings ReadSettings()
    {
        try
        {
            return _store.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogReadFailed(_logger, ex, _store.FilePath);
            return new UserSettings();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Settings could not be read from {Path}; saved image folders are not restored")]
    private static partial void LogReadFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Settings could not be saved to {Path}; the image folder list is kept for this run only")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception, string path);
}
