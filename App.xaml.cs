using System;
using System.Threading.Tasks;
using System.Windows;
using JumpListLauncher.Helpers;

namespace JumpListLauncher;

public partial class App : Application
{
    public static bool IsDarkMode { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Detect system theme
        IsDarkMode = ThemeHelper.IsDarkMode();

        // Load the correct theme dictionary
        var themeUri = IsDarkMode
            ? new Uri("Themes/DarkTheme.xaml", UriKind.Relative)
            : new Uri("Themes/LightTheme.xaml", UriKind.Relative);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = themeUri });

        // Pre-search taskbar button position in background
        Task.Run(() => WindowPositioner.PreFindTaskbarButton());

        base.OnStartup(e);
    }
}
