using CamBridge.App.Settings;
using CamBridge.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace CamBridge.Tests.Windows.App;

public sealed class ImageFolderCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cambridge-tests", Guid.NewGuid().ToString("N"));
    private readonly UserSettingsStore _store;

    public ImageFolderCatalogTests()
    {
        _store = new UserSettingsStore(Path.Combine(_root, "settings", "settings.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>The catalog only looks at file extensions, so the images need no content.</summary>
    private string Folder(string name)
    {
        string folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "img00.png"), []);
        return folder;
    }

    private ImageFolderCatalog Catalog(string? sample = null) => new(sample, _store, NullLogger.Instance);

    private static async Task<IReadOnlyList<string>> ListedAsync(ImageFolderCatalog catalog) =>
        (await catalog.Provider.EnumerateAsync()).Select(c => c.DeviceId).ToList();

    [Fact]
    public async Task Add_IsRestoredByTheNextRun()
    {
        string sample = Folder("sample");
        string added = Folder("added");

        Catalog(sample).Add(added);

        Assert.Equal([sample, added], await ListedAsync(Catalog(sample)));
        Assert.Equal([added], _store.Load().ImageFolders);
    }

    [Fact]
    public void Add_KnownFolderOrSample_SavesNothingNew()
    {
        string sample = Folder("sample");
        string added = Folder("added");
        ImageFolderCatalog catalog = Catalog(sample);

        catalog.Add(added);
        catalog.Add(added + Path.DirectorySeparatorChar);
        catalog.Add(added.ToUpperInvariant());
        catalog.Add(sample);

        Assert.Equal([added], _store.Load().ImageFolders);
    }

    [Fact]
    public void Add_CannotPlay_ThrowsAndSavesNothing()
    {
        ImageFolderCatalog catalog = Catalog();

        Assert.Throws<DirectoryNotFoundException>(() => catalog.Add(Path.Combine(_root, "missing")));

        Assert.False(File.Exists(_store.FilePath));
    }

    [Fact]
    public async Task Remove_IsKeptByTheNextRun()
    {
        string sample = Folder("sample");
        string first = Folder("first");
        string second = Folder("second");
        ImageFolderCatalog catalog = Catalog(sample);
        catalog.Add(first);
        CameraInfo removed = catalog.Add(second);

        catalog.Remove(removed);

        Assert.Equal([sample, first], await ListedAsync(catalog));
        Assert.Equal([sample, first], await ListedAsync(Catalog(sample)));
        Assert.True(File.Exists(Path.Combine(second, "img00.png")));
    }

    [Fact]
    public async Task OnlyAddedFolders_CanBeRemoved()
    {
        string sample = Folder("sample");
        string added = Folder("added");
        Catalog(sample).Add(added);
        ImageFolderCatalog catalog = Catalog(sample);
        CameraInfo sampleCamera = catalog.Add(sample);
        CameraInfo restored = (await catalog.Provider.EnumerateAsync()).Single(c => c.DeviceId == added);

        Assert.False(catalog.IsAddedFolder(sampleCamera));
        Assert.False(catalog.IsAddedFolder(new CameraInfo("pylon", "12345678", "Basler", "acA640-120gm")));
        Assert.True(catalog.IsAddedFolder(restored));
        Assert.Throws<InvalidOperationException>(() => catalog.Remove(sampleCamera));
    }

    [Fact]
    public async Task SavedFolderMissing_IsHiddenButKept()
    {
        string added = Folder("added");
        Catalog().Add(added);
        Directory.Move(added, added + "-away");

        ImageFolderCatalog catalog = Catalog();
        Assert.Empty(await ListedAsync(catalog));
        catalog.Add(Folder("other"));

        Directory.Move(added + "-away", added);
        Assert.Equal([added, Path.Combine(_root, "other")], await ListedAsync(Catalog()));
    }

    [Fact]
    public void TwoInstances_KeepEachOthersFolders()
    {
        ImageFolderCatalog first = Catalog();
        ImageFolderCatalog second = Catalog();

        first.Add(Folder("a"));
        second.Add(Folder("b"));

        Assert.Equal([Path.Combine(_root, "a"), Path.Combine(_root, "b")], _store.Load().ImageFolders);
    }

    [Fact]
    public async Task UnreadableSettings_StartEmptyAndAreReplacedOnSave()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_store.FilePath)!);
        File.WriteAllText(_store.FilePath, "not json");

        ImageFolderCatalog catalog = Catalog();
        Assert.Empty(await ListedAsync(catalog));
        string added = Folder("added");
        catalog.Add(added);

        Assert.Equal([added], _store.Load().ImageFolders);
    }
}
