using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;

namespace JumpListLauncher.Helpers;

public static class WindowPositioner
{
    private const int ABM_GETTASKBARPOS = 5;
    private const int ABE_LEFT = 0;
    private const int ABE_TOP = 1;
    private const int ABE_RIGHT = 2;
    private const int ABE_BOTTOM = 3;

    private const int MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uCallbackMessage;
        public int uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(int dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, int dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    // Cache the button rect per launch
    private static System.Windows.Rect? _cachedButtonRect;
    // Cache cursor position at startup (before WPF window steals focus)
    private static POINT _startupCursorPos;

    /// <summary>
    /// Pre-search the taskbar button position. Call this as early as possible (App.OnStartup).
    /// Also captures cursor position before WPF window appears.
    /// </summary>
    public static void PreFindTaskbarButton()
    {
        GetCursorPos(out _startupCursorPos);
        _cachedButtonRect = FindTaskbarButtonRect();
    }

    public static (double Left, double Top) CalculatePosition(Window window)
    {
        var presentationSource = PresentationSource.FromVisual(window);
        double dpiX = 1.0, dpiY = 1.0;
        if (presentationSource?.CompositionTarget != null)
        {
            dpiX = presentationSource.CompositionTarget.TransformToDevice.M11;
            dpiY = presentationSource.CompositionTarget.TransformToDevice.M22;
        }

        double winWidth = double.IsNaN(window.Width) ? window.ActualWidth : window.Width;
        double winHeight = double.IsNaN(window.Height) ? window.ActualHeight : window.Height;

        // Get the monitor where cursor is located
        var hMonitor = MonitorFromPoint(_startupCursorPos, MONITOR_DEFAULTTONEAREST);
        var monInfo = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
        GetMonitorInfo(hMonitor, ref monInfo);

        // Work area of the current monitor (excludes taskbar), in physical pixels
        double workLeft = monInfo.rcWork.left / dpiX;
        double workTop = monInfo.rcWork.top / dpiY;
        double workRight = monInfo.rcWork.right / dpiX;
        double workBottom = monInfo.rcWork.bottom / dpiY;
        double workWidth = workRight - workLeft;
        double workHeight = workBottom - workTop;

        // Monitor full area (includes taskbar)
        double monLeft = monInfo.rcMonitor.left / dpiX;
        double monTop = monInfo.rcMonitor.top / dpiY;
        double monRight = monInfo.rcMonitor.right / dpiX;
        double monBottom = monInfo.rcMonitor.bottom / dpiY;

        // Detect taskbar edge on this monitor by comparing work area vs monitor area
        int taskbarEdge = ABE_BOTTOM; // default
        double taskbarSize = 0;
        if (Math.Abs(monBottom - workBottom) > 1)
        {
            taskbarEdge = ABE_BOTTOM;
            taskbarSize = monBottom - workBottom;
        }
        else if (Math.Abs(monTop - workTop) > 1)
        {
            taskbarEdge = ABE_TOP;
            taskbarSize = workTop - monTop;
        }
        else if (Math.Abs(monLeft - workLeft) > 1)
        {
            taskbarEdge = ABE_LEFT;
            taskbarSize = workLeft - monLeft;
        }
        else if (Math.Abs(monRight - workRight) > 1)
        {
            taskbarEdge = ABE_RIGHT;
            taskbarSize = monRight - workRight;
        }

        // Use cached button rect if it's on this monitor
        var buttonRect = _cachedButtonRect;
        double iconCenterX;

        if (buttonRect.HasValue &&
            buttonRect.Value.X / dpiX >= monLeft && buttonRect.Value.X / dpiX <= monRight)
        {
            iconCenterX = (buttonRect.Value.X + buttonRect.Value.Width / 2.0) / dpiX;
        }
        else
        {
            // Fallback: use cursor X position
            iconCenterX = _startupCursorPos.x / dpiX;
        }

        double left = iconCenterX - (winWidth / 2.0);
        double top;

        switch (taskbarEdge)
        {
            case ABE_BOTTOM:
                top = workBottom - winHeight - 12;
                break;
            case ABE_TOP:
                top = workTop + 12;
                break;
            case ABE_LEFT:
                left = workLeft + 12;
                top = workTop + (workHeight - winHeight) / 2;
                break;
            case ABE_RIGHT:
                left = workRight - winWidth - 12;
                top = workTop + (workHeight - winHeight) / 2;
                break;
            default:
                top = workBottom - winHeight - 12;
                break;
        }

        // Clamp to this monitor's work area
        if (left < workLeft + 8) left = workLeft + 8;
        if (top < workTop + 8) top = workTop + 8;
        if (left + winWidth > workRight - 8)
            left = workRight - winWidth - 8;
        if (top + winHeight > workBottom - 8)
            top = workBottom - winHeight - 8;

        return (left, top);
    }

    /// <summary>
    /// Returns the work area of the monitor where the cursor is.
    /// Used by MainWindow for clamping on SizeChanged.
    /// </summary>
    public static System.Windows.Rect GetCurrentMonitorWorkArea()
    {
        GetCursorPos(out var pt);
        var hMonitor = MonitorFromPoint(pt.x != 0 || pt.y != 0 ? pt : _startupCursorPos, MONITOR_DEFAULTTONEAREST);
        var monInfo = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
        GetMonitorInfo(hMonitor, ref monInfo);

        // Use startup DPI (assume consistent per session)
        return new System.Windows.Rect(
            monInfo.rcWork.left, monInfo.rcWork.top,
            monInfo.rcWork.right - monInfo.rcWork.left,
            monInfo.rcWork.bottom - monInfo.rcWork.top);
    }

    private static System.Windows.Rect? FindTaskbarButtonRect()
    {
        try
        {
            string exeName = Process.GetCurrentProcess().ProcessName;

            var condition = new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)
            );

            // Search primary taskbar (Shell_TrayWnd)
            var result = SearchTaskbar("Shell_TrayWnd", exeName, condition);
            if (result.HasValue) return result;

            // Search secondary taskbars (Shell_SecondaryTrayWnd) for multi-monitor
            var root = AutomationElement.RootElement;
            var secondaryBars = root.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_SecondaryTrayWnd"));

            foreach (AutomationElement bar in secondaryBars)
            {
                var elements = bar.FindAll(TreeScope.Descendants, condition);
                foreach (AutomationElement el in elements)
                {
                    try
                    {
                        string name = el.Current.Name ?? "";
                        if (name.Contains(exeName, StringComparison.OrdinalIgnoreCase))
                        {
                            var rect = el.Current.BoundingRectangle;
                            if (!rect.IsEmpty && rect.Width > 0 && rect.Height > 0)
                                return rect;
                        }
                    }
                    catch { continue; }
                }
            }
        }
        catch { }
        return null;
    }

    private static System.Windows.Rect? SearchTaskbar(string className, string exeName, System.Windows.Automation.Condition condition)
    {
        try
        {
            var taskbar = AutomationElement.RootElement.FindFirst(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ClassNameProperty, className));
            if (taskbar == null) return null;

            var elements = taskbar.FindAll(TreeScope.Descendants, condition);
            foreach (AutomationElement el in elements)
            {
                try
                {
                    string name = el.Current.Name ?? "";
                    if (name.Contains(exeName, StringComparison.OrdinalIgnoreCase))
                    {
                        var rect = el.Current.BoundingRectangle;
                        if (!rect.IsEmpty && rect.Width > 0 && rect.Height > 0)
                            return rect;
                    }
                }
                catch { continue; }
            }
        }
        catch { }
        return null;
    }
}
