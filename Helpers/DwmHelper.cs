using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace JumpListLauncher.Helpers;

public static class DwmHelper
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    private const int DWMWCP_ROUND = 2;
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic (popup-style)

    public static void EnableMica(Window window)
    {
        bool isDark = App.IsDarkMode;
        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();

        // Set dark/light mode border based on system theme
        if (Environment.OSVersion.Version.Build >= 22000)
        {
            int darkMode = isDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            int cornerPref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));
        }

        // Acrylic backdrop (Win11 22H2+)
        if (Environment.OSVersion.Version.Build >= 22621)
        {
            int backdropType = DWMSBT_TRANSIENTWINDOW;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));

            var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);

            var hwndSource = HwndSource.FromHwnd(hwnd);
            if (hwndSource?.CompositionTarget != null)
            {
                hwndSource.CompositionTarget.BackgroundColor = Colors.Transparent;
            }

            window.Background = Brushes.Transparent;
        }
        else
        {
            // Fallback for older Windows
            window.Background = isDark
                ? new SolidColorBrush(Color.FromArgb(0xE8, 0x2C, 0x2C, 0x2C))
                : new SolidColorBrush(Color.FromArgb(0xE8, 0xF3, 0xF3, 0xF3));
        }
    }
}
