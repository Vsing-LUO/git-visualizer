namespace GitVisualizer.Infrastructure;

public static class LocalPaths
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitVisualizer");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string DatabaseFile => Path.Combine(Root, "state.db");
    public static string RecoveryDirectory => Path.Combine(Root, "Recovery");
    public static string LogDirectory => Path.Combine(Root, "Logs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(RecoveryDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
