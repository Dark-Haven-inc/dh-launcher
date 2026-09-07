namespace DarkHaven.Launcher;

/// <summary>
/// Resolves the on-disk locations the launcher uses. Override the root with the
/// <c>DH_LAUNCHER_DATA</c> env var, or append a suffix with <c>SS14_LAUNCHER_APPDATA_NAME</c>
/// (kept for parity with the reference launcher's dev workflow).
/// </summary>
public static class LauncherPaths
{
    public static string DataDir { get; } = ResolveDataDir();

    public static string EnginesDir => Path.Combine(DataDir, "engines");
    public static string ModulesDir => Path.Combine(DataDir, "modules");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string ContentDbPath => Path.Combine(DataDir, "content.db");
    public static string SettingsDbPath => Path.Combine(DataDir, "settings.db");

    /// <summary>The SS14 engine signing public key, shipped next to the launcher assembly.</summary>
    public static string SigningKeyPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "signing_key");

    /// <summary>Bundled fallback list of Dark Haven regions.</summary>
    public static string RegionsJsonPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "dh-regions.json");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(EnginesDir);
        Directory.CreateDirectory(ModulesDir);
        Directory.CreateDirectory(LogsDir);
    }

    private static string ResolveDataDir()
    {
        var overrideDir = Environment.GetEnvironmentVariable("DH_LAUNCHER_DATA");
        if (!string.IsNullOrWhiteSpace(overrideDir))
            return Path.GetFullPath(overrideDir);

        string root;
        if (OperatingSystem.IsWindows())
            root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        else if (OperatingSystem.IsMacOS())
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support");
        else
            root = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                   ?? Path.Combine(
                       Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

        var name = "DarkHavenLauncher";
        var suffix = Environment.GetEnvironmentVariable("SS14_LAUNCHER_APPDATA_NAME");
        if (!string.IsNullOrWhiteSpace(suffix))
            name += "_" + suffix;

        return Path.Combine(root, name);
    }
}
