namespace DependencyManager.Storage;

internal static class StoragePaths
{
    public static string DataDirectory => Path.Combine(AppContext.BaseDirectory, "Data");
    public static string DatabasePath => Path.Combine(DataDirectory, "DependencyManager.db");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        string probe = Path.Combine(DataDirectory, $"write-test-{Guid.NewGuid():N}.tmp");
        using (File.Create(probe)) { }
        File.Delete(probe);
    }
}
