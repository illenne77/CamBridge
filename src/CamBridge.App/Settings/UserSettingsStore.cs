using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CamBridge.App.Settings;

/// <summary>Per-user state kept across runs. Unknown JSON properties are ignored, so it can grow.</summary>
public sealed record UserSettings
{
    /// <summary>Image folders the user added, as full paths in the order they were added.</summary>
    public IReadOnlyList<string> ImageFolders { get; init; } = [];
}

/// <summary>
/// Reads and writes <see cref="UserSettings"/> as JSON. The default file lives under
/// %LOCALAPPDATA%, so it survives a clean build and paths of customer images never enter the
/// repository working tree.
/// </summary>
public sealed class UserSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keeps Korean paths readable; never embedded in HTML
    };

    public UserSettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        FilePath = Path.GetFullPath(filePath);
    }

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CamBridge", "settings.json");

    public string FilePath { get; }

    /// <summary>Returns defaults when the file does not exist yet.</summary>
    /// <exception cref="JsonException">The file is not a settings file this version can use.</exception>
    public UserSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            return new UserSettings();
        }

        using FileStream stream = File.OpenRead(FilePath);
        UserSettings? settings = JsonSerializer.Deserialize<UserSettings>(stream, JsonOptions);

        // The serializer does not enforce nullability, and a hand-edited file can hold anything.
        if (settings?.ImageFolders is not { } folders || folders.Any(string.IsNullOrWhiteSpace))
        {
            throw new JsonException($"{FilePath} does not contain a valid image folder list.");
        }

        return settings;
    }

    /// <summary>
    /// Writes a temporary file and then replaces the old one, so a crash during the write leaves
    /// the previous settings intact.
    /// </summary>
    public void Save(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, FilePath, overwrite: true);
    }
}
