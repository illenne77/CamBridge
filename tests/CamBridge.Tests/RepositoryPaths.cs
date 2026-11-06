namespace CamBridge.Tests;

/// <summary>Locates files committed to the repository (such as samples/) from the test output directory.</summary>
internal static class RepositoryPaths
{
    public static string Root()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CamBridge.sln")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root (CamBridge.sln) not found.");
    }

    public static string Sample(string name) => Path.Combine(Root(), "samples", name);
}
