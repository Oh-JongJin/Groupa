using Microsoft.Win32;

namespace JumpListLauncher.Helpers;

public static class ThemeHelper
{
    /// <summary>
    /// Returns true if Windows is set to dark mode for apps.
    /// Registry: HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme
    /// 0 = Dark, 1 = Light
    /// </summary>
    public static bool IsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int intValue)
                return intValue == 0;
        }
        catch { }

        return true; // default to dark if can't detect
    }
}
