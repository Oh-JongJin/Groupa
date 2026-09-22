using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Shell;
using JumpListLauncher.Helpers;

namespace JumpListLauncher;

public partial class App : Application
{
    public static bool IsDarkMode { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Load preferences
        var prefs = PreferencesHelper.Load();

        // Apply language preference (must be before any Strings.Get() call)
        Strings.SetLanguage(prefs.Language);

        // Detect theme (respects preference: system/dark/light)
        IsDarkMode = PreferencesHelper.ResolveDarkMode(prefs);

        // Load the correct theme dictionary
        var themeUri = IsDarkMode
            ? new Uri("Themes/DarkTheme.xaml", UriKind.Relative)
            : new Uri("Themes/LightTheme.xaml", UriKind.Relative);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = themeUri });

        // Pre-search taskbar button position in background
        Task.Run(() => WindowPositioner.PreFindTaskbarButton());

        base.OnStartup(e);

        // Register Jump List (right-click menu on taskbar)
        RegisterJumpList();

        // Handle command-line arguments
        if (e.Args.Length > 0)
        {
            var configPath = AppPaths.ConfigPath;

            if (e.Args[0] == "--settings")
            {
                var editor = new ConfigEditorWindow(configPath);
                editor.ShowDialog();
                Shutdown();
                return;
            }
            else if (e.Args[0] == "--preferences")
            {
                var prefsWindow = new PreferencesWindow();
                prefsWindow.ShowDialog();
                Shutdown();
                return;
            }
        }
    }

    private void RegisterJumpList()
    {
        var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
        var jumpList = new JumpList();

        var settingsTask = new JumpTask
        {
            Title = Strings.Get("EditorTitle"),
            Description = Strings.Get("EditorTitle"),
            ApplicationPath = exePath,
            Arguments = "--settings",
            CustomCategory = ""
        };

        var prefsTask = new JumpTask
        {
            Title = Strings.Get("Preferences"),
            Description = Strings.Get("Preferences"),
            ApplicationPath = exePath,
            Arguments = "--preferences",
            CustomCategory = ""
        };

        jumpList.JumpItems.Add(settingsTask);
        jumpList.JumpItems.Add(prefsTask);
        JumpList.SetJumpList(this, jumpList);
    }
}
