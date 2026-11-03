using System.Text.Json;
using CamBridge.App.Settings;

namespace CamBridge.Tests.Windows.App;

public sealed class UserSettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cambridge-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private UserSettingsStore Store() => new(Path.Combine(_root, "nested", "settings.json"));

    [Fact]
    public void Load_NoFile_ReturnsDefaults()
    {
        Assert.Empty(Store().Load().ImageFolders);
    }

    [Fact]
    public void Save_CreatesFolderAndRoundTrips()
    {
        UserSettingsStore store = Store();
        var settings = new UserSettings { ImageFolders = [@"D:\parts\한글", @"\\server\share\tiếng-việt-😀"] };

        store.Save(settings);

        Assert.Equal(settings.ImageFolders, Store().Load().ImageFolders);
        Assert.Contains("한글", File.ReadAllText(store.FilePath), StringComparison.Ordinal); // readable when opened by hand
        Assert.Equal(["settings.json"], Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!).Select(Path.GetFileName));
    }

    [Fact]
    public void Save_ReplacesTheOldContent()
    {
        UserSettingsStore store = Store();
        store.Save(new UserSettings { ImageFolders = [@"D:\a", @"D:\b"] });

        store.Save(new UserSettings { ImageFolders = [@"D:\c"] });

        Assert.Equal([@"D:\c"], store.Load().ImageFolders);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""{ "imageFolders": null }""")]
    [InlineData("""{ "imageFolders": [ "D:\\a", "" ] }""")]
    [InlineData("""{ "imageFolders": "D:\\a" }""")]
    public void Load_InvalidContent_ThrowsJsonException(string content)
    {
        UserSettingsStore store = Store();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, content);

        Assert.ThrowsAny<JsonException>(store.Load);
    }

    [Fact]
    public void Load_UnknownProperties_AreIgnored()
    {
        UserSettingsStore store = Store();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, """{ "imageFolders": [ "D:\\a" ], "windowWidth": 1280 }""");

        Assert.Equal([@"D:\a"], store.Load().ImageFolders);
    }
}
