using System.Windows;
using System.Windows.Input;

namespace JumpListLauncher.Helpers;

/// <summary>
/// Hides mouse-hover highlights while the user navigates with the keyboard, so the hovered item and the
/// keyboard-focused/selected item don't both look highlighted. The flag is inherited down the visual tree;
/// item templates show hover only when <c>IsMouseOver</c> is true and <see cref="SuppressProperty"/> is false.
/// It is set by arrow/page/home/end/tab keys and cleared as soon as the mouse actually moves.
/// </summary>
public static class HoverHelper
{
    public static readonly DependencyProperty SuppressProperty =
        DependencyProperty.RegisterAttached("Suppress", typeof(bool), typeof(HoverHelper),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetSuppress(DependencyObject d) => (bool)d.GetValue(SuppressProperty);
    public static void SetSuppress(DependencyObject d, bool value) => d.SetValue(SuppressProperty, value);

    public static void Attach(Window window)
    {
        Point? lastMouse = null;

        window.PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.Up or Key.Down or Key.Left or Key.Right or Key.PageUp or Key.PageDown
                or Key.Home or Key.End or Key.Tab)
            {
                SetSuppress(window, true);
            }
        };

        // MouseMove also fires without real movement (e.g. when items scroll or move under a still
        // cursor), so only a changed screen position counts.
        window.PreviewMouseMove += (_, e) =>
        {
            var pos = window.PointToScreen(e.GetPosition(window));
            if (lastMouse is { } prev && prev == pos) return;
            lastMouse = pos;
            if (GetSuppress(window)) SetSuppress(window, false);
        };
    }
}
