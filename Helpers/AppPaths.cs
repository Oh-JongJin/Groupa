using System;
using System.IO;

namespace JumpListLauncher.Helpers;

public static class AppPaths
{
    private static readonly string _dataDir;

    static AppPaths()
    {
        _dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Groupa");

        if (!Directory.Exists(_dataDir))
            Directory.CreateDirectory(_dataDir);

        // Migrate: if config exists next to exe, move to AppData
        MigrateIfNeeded("config.json");
        MigrateIfNeeded("preferences.json");
    }

    public static string ConfigPath => Path.Combine(_dataDir, "config.json");
    public static string PreferencesPath => Path.Combine(_dataDir, "preferences.json");

    private static void MigrateIfNeeded(string fileName)
    {
        var oldPath = Path.Combine(AppContext.BaseDirectory, fileName);
        var newPath = Path.Combine(_dataDir, fileName);

        if (File.Exists(oldPath) && !File.Exists(newPath))
        {
            try { File.Move(oldPath, newPath); } catch { }
        }
    }
}
