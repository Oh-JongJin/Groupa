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

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(int dwMessage, ref APPBARDATA pData);

    // Cache the button rect — search once per app launch, reuse in CalculatePosition
    private static System.Windows.Rect? _cachedButtonRect;

    /// <summary>
    /// Pre-search the taskbar button position. Call this as early as possible (App.OnStartup).
    /// Runs fast since it only looks for Button/ListItem elements.
    /// </summary>
    public static void PreFindTaskbarButton()
    {
        _cachedButtonRect = FindTaskbarButtonRect();
    }

    public static (double Left, double Top) CalculatePosition(Window window)
    {
        var appBarData = new APPBARDATA();
        appBarData.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
        SHAppBarMessage(ABM_GETTASKBARPOS, ref appBarData);

        var presentationSource = PresentationSource.FromVisual(window);
        double dpiX = 1.0, dpiY = 1.0;
        if (presentationSource?.CompositionTarget != null)
        {
            dpiX = presentationSource.CompositionTarget.TransformToDevice.M11;
            dpiY = presentationSource.CompositionTarget.TransformToDevice.M22;
        }

        double winWidth = double.IsNaN(window.Width) ? window.ActualWidth : window.Width;
        double winHeight = double.IsNaN(window.Height) ? window.ActualHeight : window.Height;

        // Use cached button rect, or search now if not cached
        var buttonRect = _cachedButtonRect ?? FindTaskbarButtonRect();

        double iconCenterX;
        if (buttonRect.HasValue)
        {
            iconCenterX = (buttonRect.Value.X + buttonRect.Value.Width / 2.0) / dpiX;
        }
        else
        {
            iconCenterX = (appBarData.rc.left + appBarData.rc.right) / 2.0 / dpiX;
        }

        double taskbarTop = appBarData.rc.top / dpiY;
        double taskbarBottom = appBarData.rc.bottom / dpiY;
        double taskbarLeft = appBarData.rc.left / dpiX;
        double taskbarRight = appBarData.rc.right / dpiX;

        double workAreaWidth = SystemParameters.WorkArea.Width;
        double workAreaHeight = SystemParameters.WorkArea.Height;
        double workAreaLeft = SystemParameters.WorkArea.Left;
        double workAreaTop = SystemParameters.WorkArea.Top;

        double left = iconCenterX - (winWidth / 2.0);
        double top;

        switch (appBarData.uEdge)
        {
            case ABE_BOTTOM:
                top = taskbarTop - winHeight - 12;
                break;
            case ABE_TOP:
                top = taskbarBottom + 12;
                break;
            case ABE_LEFT:
                left = taskbarRight + 12;
                top = workAreaTop + (workAreaHeight - winHeight) / 2;
                break;
            case ABE_RIGHT:
                left = taskbarLeft - winWidth - 12;
                top = workAreaTop + (workAreaHeight - winHeight) / 2;
                break;
            default:
                top = workAreaTop + workAreaHeight - winHeight - 12;
                break;
        }

        if (left < workAreaLeft + 8) left = workAreaLeft + 8;
        if (top < workAreaTop + 8) top = workAreaTop + 8;
        if (left + winWidth > workAreaLeft + workAreaWidth - 8)
            left = workAreaLeft + workAreaWidth - winWidth - 8;
        if (top + winHeight > workAreaTop + workAreaHeight - 8)
            top = workAreaTop + workAreaHeight - winHeight - 8;

        return (left, top);
    }

    private static System.Windows.Rect? FindTaskbarButtonRect()
    {
        try
        {
            var taskbar = AutomationElement.RootElement.FindFirst(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"));
            if (taskbar == null) return null;

            string exeName = Process.GetCurrentProcess().ProcessName;

            // PERF: Only search Button and ListItem elements (not every descendant)
            var condition = new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)
            );
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
