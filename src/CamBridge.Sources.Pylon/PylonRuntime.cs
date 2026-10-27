namespace CamBridge.Sources.Pylon;

/// <summary>
/// Makes the pylon native runtime and emulator setting visible to processes started before
/// pylon was installed (or before PYLON_CAMEMU was set), e.g. an IDE or a long-lived shell:
/// their inherited environment lacks the installer's PATH entry, so loading
/// Basler.Pylon.ExternC.dll fails. Only the current process environment is changed.
/// </summary>
internal static class PylonRuntime
{
    private const string NativeLibrary = "Basler.Pylon.ExternC.dll";

    public static void PrepareEnvironment()
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string[] entries = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (!entries.Any(ContainsNativeLibrary) && FindRuntimeDirectory() is string runtime)
        {
            Environment.SetEnvironmentVariable("PATH", runtime + Path.PathSeparator + path);
        }

        if (Environment.GetEnvironmentVariable("PYLON_CAMEMU") is null && PersistedVariable("PYLON_CAMEMU") is string emulators)
        {
            Environment.SetEnvironmentVariable("PYLON_CAMEMU", emulators);
        }
    }

    private static string? FindRuntimeDirectory()
    {
        // The installer sets PYLON_DEV_DIR to <pylon>\Development; the runtime sits next to it.
        foreach (string? devDir in new[] { Environment.GetEnvironmentVariable("PYLON_DEV_DIR"), PersistedVariable("PYLON_DEV_DIR") })
        {
            if (!string.IsNullOrWhiteSpace(devDir))
            {
                string candidate = Path.GetFullPath(Path.Combine(devDir, "..", "Runtime", "x64"));
                if (ContainsNativeLibrary(candidate))
                {
                    return candidate;
                }
            }
        }

        // Fall back to the persisted PATH, which the installer updates.
        return PersistedVariable("PATH")?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(ContainsNativeLibrary);
    }

    private static string? PersistedVariable(string name) =>
        Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
        ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);

    private static bool ContainsNativeLibrary(string directory)
    {
        try
        {
            return File.Exists(Path.Combine(directory, NativeLibrary));
        }
        catch (ArgumentException)
        {
            return false; // malformed PATH entry
        }
    }
}
