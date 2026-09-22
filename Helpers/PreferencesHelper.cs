using System;
using System.IO;
using System.Text.Json;
using JumpListLauncher.Models;

namespace JumpListLauncher.Helpers;

public static class PreferencesHelper
{
    private static readonly string _path = AppPaths.PreferencesPath;
    private static Preferences? _cached;

    public static Preferences Load()
    {
        if (_cached != null) return _cached;

        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                _cached = JsonSerializer.Deserialize<Preferences>(json) ?? new Preferences();
                return _cached;
            }
        }
        catch { }

        _cached = new Preferences();
        return _cached;
    }

    public static void Save(Preferences prefs)
    {
        _cached = prefs;
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        File.WriteAllText(_path, JsonSerializer.Serialize(prefs, options));
    }

    /// <summary>
    /// Resolve theme: returns true if dark mode should be used.
    /// </summary>
    public static bool ResolveDarkMode(Preferences prefs)
    {
        return prefs.Theme switch
        {
            "dark" => true,
            "light" => false,
            _ => ThemeHelper.IsDarkMode() // "system"
        };
    }
}
