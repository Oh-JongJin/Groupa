using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace JumpListLauncher;

/// <summary>
/// Scrolling the app list while something is dragged over it (files from Explorer or an item being reordered):
/// auto-scroll when the cursor is near the top/bottom edge, and mouse-wheel scrolling.
/// During a drag Windows doesn't deliver wheel messages to the drop target (the drag source's window has
/// focus, e.g. Explorer), so a low-level mouse hook is installed for the duration of the drag only, and it
/// consumes wheel events only while the cursor is over this list.
/// </summary>
public partial class ConfigEditorWindow
{
    private const double AutoScrollEdge = 28;          // px from the list's top/bottom edge
    private static readonly TimeSpan AutoScrollTick = TimeSpan.FromMilliseconds(60);

    private DispatcherTimer? _dragTimer;
    private bool _dragSessionActive;
    private bool _dragIsFiles;
    private AppEditItem? _draggedItem;
    private DateTime _dragLeftAt = DateTime.MaxValue;
    private int _autoScrollSkip;

    private ScrollViewer? _listScroll;
    private ScrollViewer? ListScroll => _listScroll ??= FindChild<ScrollViewer>(AppListBox);

    /// <summary>Called on every DragEnter/DragOver over the list.</summary>
    private void TouchDragSession(bool isFiles, AppEditItem? dragged)
    {
        _dragIsFiles = isFiles;
        _draggedItem = dragged;
        _dragLeftAt = DateTime.MaxValue;
        if (_dragSessionActive) return;

        _dragSessionActive = true;
        InstallWheelHook();
        _dragTimer ??= new DispatcherTimer(DispatcherPriority.Input) { Interval = AutoScrollTick };
        _dragTimer.Tick -= DragTimer_Tick;
        _dragTimer.Tick += DragTimer_Tick;
        _dragTimer.Start();
    }

    /// <summary>DragLeave: end the session unless the drag comes back right away (crossing child elements).</summary>
    private void LeaveDragSession() => _dragLeftAt = DateTime.UtcNow;

    private void EndDragSession()
    {
        if (!_dragSessionActive) return;
        _dragSessionActive = false;
        _draggedItem = null;
        _dragTimer?.Stop();
        RemoveWheelHook();
        HideDropLine();
    }

    private void DragTimer_Tick(object? sender, EventArgs e)
    {
        // Drag over (dropped, cancelled, or left the list for good)
        bool buttonDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 || (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;
        if (!buttonDown || (DateTime.UtcNow - _dragLeftAt).TotalMilliseconds > 150)
        {
            EndDragSession();
            return;
        }
        if (_dragLeftAt != DateTime.MaxValue) return; // outside the list right now

        var pos = CursorInList();
        if (pos == null || ListScroll == null) return;

        // Faster the closer to the edge: every tick at the very edge, every 3rd tick at the outer band
        double y = pos.Value.Y, h = AppListBox.ActualHeight;
        int direction = y < AutoScrollEdge ? -1 : y > h - AutoScrollEdge ? 1 : 0;
        if (direction == 0) { _autoScrollSkip = 0; return; }

        double depth = direction < 0 ? AutoScrollEdge - y : y - (h - AutoScrollEdge);
        int every = depth > AutoScrollEdge * 0.66 ? 1 : depth > AutoScrollEdge * 0.33 ? 2 : 3;
        if (++_autoScrollSkip < every) return;
        _autoScrollSkip = 0;

        if (direction < 0) ListScroll.LineUp(); else ListScroll.LineDown();
        AfterDragScroll();
    }

    /// <summary>After the list scrolled under a still cursor, refresh the drop line / live reorder.</summary>
    private void AfterDragScroll()
    {
        AppListBox.UpdateLayout();
        var pos = CursorInList();
        if (pos == null) return;
        if (_dragIsFiles) ShowDropLine(DropIndexAt(pos.Value));
        else if (_draggedItem != null) ReorderDraggedTo(pos.Value, _draggedItem);
    }

    private Point? CursorInList()
    {
        if (!GetCursorPos(out var p)) return null;
        try
        {
            var pt = AppListBox.PointFromScreen(new Point(p.x, p.y));
            if (pt.X < 0 || pt.X > AppListBox.ActualWidth || pt.Y < -AutoScrollEdge || pt.Y > AppListBox.ActualHeight + AutoScrollEdge)
                return null;
            return pt;
        }
        catch { return null; } // not connected to a presentation source
    }

    private bool IsCursorOverList(POINT screen)
    {
        try
        {
            var pt = AppListBox.PointFromScreen(new Point(screen.x, screen.y));
            return pt.X >= 0 && pt.Y >= 0 && pt.X <= AppListBox.ActualWidth && pt.Y <= AppListBox.ActualHeight;
        }
        catch { return false; }
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }

    // === Low-level mouse hook (drag duration only) ===

    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private IntPtr _wheelHook;
    private LowLevelMouseProc? _wheelHookProc; // keep the delegate alive while hooked

    private void InstallWheelHook()
    {
        if (_wheelHook != IntPtr.Zero) return;
        _wheelHookProc = WheelHookProc;
        _wheelHook = SetWindowsHookEx(WH_MOUSE_LL, _wheelHookProc, GetModuleHandle(null), 0);
    }

    private void RemoveWheelHook()
    {
        if (_wheelHook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_wheelHook);
        _wheelHook = IntPtr.Zero;
        _wheelHookProc = null;
    }

    // Runs on the UI thread (the hook's thread) — keep it short.
    private IntPtr WheelHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_MOUSEWHEEL && _dragSessionActive)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            // Hook coordinates are per-monitor physical pixels; GetCursorPos matches this process's DPI mode
            if (GetCursorPos(out var cursor) && IsCursorOverList(cursor) && ListScroll != null)
            {
                short delta = (short)((info.mouseData >> 16) & 0xFFFF);
                int lines = Math.Max(1, SystemParameters.WheelScrollLines) * Math.Max(1, Math.Abs(delta) / 120);
                for (int i = 0; i < lines; i++)
                {
                    if (delta > 0) ListScroll.LineUp(); else ListScroll.LineDown();
                }
                AfterDragScroll();
                return (IntPtr)1; // consumed: don't also scroll the window under the cursor
            }
        }
        return CallNextHookEx(_wheelHook, nCode, wParam, lParam);
    }
}
