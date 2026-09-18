namespace GitVisualizer.Infrastructure;

/// <summary>Immutable storage paths; inject an instance to isolate a service.</summary>
public sealed class LocalDataPaths
{
    public LocalDataPaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Path.IsPathFullyQualified(root))
            throw new ArgumentException("A fully qualified data directory is required.", nameof(root));
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }
    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string DatabaseFile => Path.Combine(Root, "state.db");
    public string RecoveryDirectory => Path.Combine(Root, "Recovery");
    public string DraftDirectory => Path.Combine(Root, "Drafts");
    public string LogDirectory => Path.Combine(Root, "Logs");
    public bool IsIsolated => !Root.Equals(LocalPaths.ProductionRoot, StringComparison.OrdinalIgnoreCase);

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(RecoveryDirectory);
        Directory.CreateDirectory(DraftDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}

/// <summary>The process override must be set before first use.</summary>
public static class LocalPaths
{
    public static string ProductionRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitVisualizer");
    public static LocalDataPaths Default { get; } = new(
        Environment.GetEnvironmentVariable("GITVISUALIZER_DATA_ROOT") ?? ProductionRoot);
    public static string Root => Default.Root;
    public static string SettingsFile => Default.SettingsFile;
    public static string DatabaseFile => Default.DatabaseFile;
    public static string RecoveryDirectory => Default.RecoveryDirectory;
    public static string DraftDirectory => Default.DraftDirectory;
    public static string LogDirectory => Default.LogDirectory;
    public static void EnsureCreated() => Default.EnsureCreated();
}
