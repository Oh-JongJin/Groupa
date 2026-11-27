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

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    private const uint GA_ROOT = 2;

    // Taskbar button rect used for positioning. Written from background threads, so guard with a lock.
    private static readonly object _rectLock = new();
    private static System.Windows.Rect? _cachedButtonRect;
    // Cursor position captured right before the popup is shown (before WPF window steals focus)
    private static POINT _startupCursorPos;

    private static System.Windows.Rect? CachedButtonRect
    {
        get { lock (_rectLock) return _cachedButtonRect; }
        set { lock (_rectLock) _cachedButtonRect = value; }
    }

    /// <summary>
    /// Capture cursor position and resolve the taskbar button right before showing the popup.
    /// Taskbar icons move whenever apps open/close (especially with a centered Windows 11 taskbar),
    /// so a rect cached at process start goes stale in background mode.
    /// </summary>
    public static async Task UpdateCursorPositionAsync()
    {
        GetCursorPos(out _startupCursorPos);
        var pt = _startupCursorPos;

        var taskbarHwnd = GetTaskbarUnderPoint(pt);
        _openedFromTaskbar = taskbarHwnd != IntPtr.Zero;
        if (taskbarHwnd != IntPtr.Zero)
        {
            // Launched by clicking a taskbar/tray button: the button under the cursor is the anchor.
            // UIA client calls must NOT run on (or block) the UI thread — they can deadlock it — so
            // look it up on a worker thread and await with a short timeout while the UI thread keeps
            // pumping. If it can't be resolved in time, fall back to the cursor X (still accurate)
            // instead of a stale cache.
            var lookup = Task.Run(() => FindButtonAtPoint(taskbarHwnd, pt));
            var done = await Task.WhenAny(lookup, Task.Delay(300));
            CachedButtonRect = done == lookup ? lookup.Result?.Rect : null;
        }
        else
        {
            // Hotkey etc.: refresh the cached rect in background; it usually finishes before Loaded.
            _ = Task.Run(RefreshTaskbarButtonCache);
        }
    }

    /// <summary>
    /// Search the taskbar for this app's button and cache its rect. Safe to call from a background thread.
    /// </summary>
    public static void RefreshTaskbarButtonCache()
    {
        var rect = FindTaskbarButtonRect();
        if (rect.HasValue) CachedButtonRect = rect;
    }

    /// <summary>
    /// True if the cursor is currently over a taskbar (including the tray area). Pure Win32, UI-thread safe.
    /// </summary>
    public static bool IsCursorOverTaskbar()
    {
        GetCursorPos(out var pt);
        return GetTaskbarUnderPoint(pt) != IntPtr.Zero;
    }

    /// <summary>
    /// Checks (off the UI thread) whether the cursor is on this app's own taskbar button,
    /// i.e. the click that will relaunch us and signal "show popup". Captures the cursor now.
    /// </summary>
    public static Task<bool> IsCursorOnOwnTaskbarButtonAsync()
    {
        GetCursorPos(out var pt);
        var taskbarHwnd = GetTaskbarUnderPoint(pt);
        if (taskbarHwnd == IntPtr.Zero) return Task.FromResult(false);

        return Task.Run(() =>
        {
            var hit = FindButtonAtPoint(taskbarHwnd, pt);
            if (hit == null) return false;
            string exeName = Process.GetCurrentProcess().ProcessName;
            // Pinned/running taskbar buttons are named "<app> ..."; skip tray icons whose tooltip
            // just mentions the app (the tray click is handled in-process).
            return hit.Value.ClassName.StartsWith("Taskbar.", StringComparison.Ordinal) &&
                   hit.Value.Name.StartsWith(exeName, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// Returns the taskbar window (Shell_TrayWnd / Shell_SecondaryTrayWnd) under the point, or IntPtr.Zero.
    /// Pure Win32, safe on the UI thread.
    /// </summary>
    private static IntPtr GetTaskbarUnderPoint(POINT pt)
    {
        try
        {
            var hwnd = WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;
            var root = GetAncestor(hwnd, GA_ROOT);
            if (root == IntPtr.Zero) root = hwnd;
            var sb = new System.Text.StringBuilder(64);
            GetClassName(root, sb, sb.Capacity);
            var cls = sb.ToString();
            return cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd" ? root : IntPtr.Zero;
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>
    /// Finds the taskbar button under the point. The Windows 11 taskbar is a XAML island, so
    /// AutomationElement.FromPoint stops at the Shell_TrayWnd pane; instead enumerate the taskbar's
    /// buttons and pick the smallest one containing the point. Must run off the UI thread.
    /// </summary>
    private static (System.Windows.Rect Rect, string Name, string ClassName)? FindButtonAtPoint(IntPtr taskbarHwnd, POINT pt)
    {
        try
        {
            var taskbar = AutomationElement.FromHandle(taskbarHwnd);
            var condition = new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));

            (System.Windows.Rect Rect, string Name, string ClassName)? best = null;
            foreach (AutomationElement el in taskbar.FindAll(TreeScope.Descendants, condition))
            {
                try
                {
                    var rect = el.Current.BoundingRectangle;
                    if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) continue;
                    if (!rect.Contains(pt.x, pt.y)) continue;
                    if (best == null || rect.Width * rect.Height < best.Value.Rect.Width * best.Value.Rect.Height)
                        best = (rect, el.Current.Name ?? "", el.Current.ClassName ?? "");
                }
                catch { }
            }
            return best;
        }
        catch { }
        return null;
    }

    public static (double Left, double Top) CalculatePosition(Window window)
    {
        if (_startupCursorPos.x == 0 && _startupCursorPos.y == 0)
        {
            GetCursorPos(out _startupCursorPos);
        }

        var presentationSource = PresentationSource.FromVisual(window);
        double dpiX = 1.0, dpiY = 1.0;
        if (presentationSource?.CompositionTarget != null)
        {
            dpiX = presentationSource.CompositionTarget.TransformToDevice.M11;
            dpiY = presentationSource.CompositionTarget.TransformToDevice.M22;
        }

        double winWidth = double.IsNaN(window.Width) ? window.ActualWidth : window.Width;
        double winHeight = double.IsNaN(window.Height) ? window.ActualHeight : window.Height;

        // "At mouse cursor" preference (not for taskbar/tray clicks, which always anchor to the button)
        if (!_openedFromTaskbar && PreferencesHelper.Load().PopupPosition == "cursor")
            return CalculateCursorPosition(winWidth, winHeight, dpiX, dpiY);

        // Anchor: our taskbar button wherever it is (with several monitors the taskbar may be on another
        // one than the cursor, e.g. hotkey pressed on a monitor without a taskbar). Without a known
        // button, use the cursor's monitor if it has a taskbar, else the main taskbar's monitor.
        var buttonRect = CachedButtonRect;
        POINT anchor = _startupCursorPos;
        bool centerOnWorkArea = false;
        if (buttonRect.HasValue)
        {
            anchor = new POINT
            {
                x = (int)(buttonRect.Value.X + buttonRect.Value.Width / 2.0),
                y = (int)(buttonRect.Value.Y + buttonRect.Value.Height / 2.0)
            };
        }
        else if (!MonitorHasTaskbar(MonitorFromPoint(_startupCursorPos, MONITOR_DEFAULTTONEAREST))
                 && GetMainTaskbarRect() is { } tb)
        {
            anchor = new POINT { x = (tb.left + tb.right) / 2, y = (tb.top + tb.bottom) / 2 };
            centerOnWorkArea = true;
        }

        var hMonitor = MonitorFromPoint(anchor, MONITOR_DEFAULTTONEAREST);
        _anchorMonitor = hMonitor;
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

        double iconCenterX;
        if (buttonRect.HasValue)
            iconCenterX = (buttonRect.Value.X + buttonRect.Value.Width / 2.0) / dpiX;
        else if (centerOnWorkArea)
            iconCenterX = workLeft + workWidth / 2.0;
        else
            iconCenterX = _startupCursorPos.x / dpiX; // cursor's monitor has a taskbar: cursor X

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
        // The monitor the popup was placed on (may differ from the cursor's)
        var hMonitor = _anchorMonitor;
        if (hMonitor == IntPtr.Zero)
        {
            GetCursorPos(out var pt);
            hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        }
        var monInfo = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
        GetMonitorInfo(hMonitor, ref monInfo);

        // Use startup DPI (assume consistent per session)
        return new System.Windows.Rect(
            monInfo.rcWork.left, monInfo.rcWork.top,
            monInfo.rcWork.right - monInfo.rcWork.left,
            monInfo.rcWork.bottom - monInfo.rcWork.top);
    }

    private static IntPtr _anchorMonitor;
    private static bool _openedFromTaskbar;

    /// <summary>Popup centered on the cursor, just above it (below if there's no room), on the cursor's monitor.</summary>
    private static (double Left, double Top) CalculateCursorPosition(double winWidth, double winHeight, double dpiX, double dpiY)
    {
        var hMonitor = MonitorFromPoint(_startupCursorPos, MONITOR_DEFAULTTONEAREST);
        _anchorMonitor = hMonitor;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
        GetMonitorInfo(hMonitor, ref mi);

        double workLeft = mi.rcWork.left / dpiX, workTop = mi.rcWork.top / dpiY;
        double workRight = mi.rcWork.right / dpiX, workBottom = mi.rcWork.bottom / dpiY;
        double cx = _startupCursorPos.x / dpiX, cy = _startupCursorPos.y / dpiY;

        double left = cx - winWidth / 2.0;
        double top = cy - winHeight - 8;
        if (top < workTop + 8) top = cy + 8;

        if (left < workLeft + 8) left = workLeft + 8;
        if (left + winWidth > workRight - 8) left = workRight - winWidth - 8;
        if (top + winHeight > workBottom - 8) top = workBottom - winHeight - 8;
        if (top < workTop + 8) top = workTop + 8;
        return (left, top);
    }

    /// <summary>True if the monitor's work area is smaller than the monitor, i.e. a taskbar is docked on it.</summary>
    private static bool MonitorHasTaskbar(IntPtr hMonitor)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
        if (!GetMonitorInfo(hMonitor, ref mi)) return true;
        return mi.rcWork.left != mi.rcMonitor.left || mi.rcWork.top != mi.rcMonitor.top ||
               mi.rcWork.right != mi.rcMonitor.right || mi.rcWork.bottom != mi.rcMonitor.bottom;
    }

    /// <summary>Screen rect of the main taskbar (Shell_TrayWnd), in physical pixels.</summary>
    private static RECT? GetMainTaskbarRect()
    {
        var abd = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)) };
        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref abd) == IntPtr.Zero) return null;
        return abd.rc;
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
