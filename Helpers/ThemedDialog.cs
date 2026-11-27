using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace JumpListLauncher.Helpers;

/// <summary>
/// Drop-in replacement for MessageBox.Show that follows the app theme (dark/light/high contrast).
/// The Win32 message box always renders in the light system style.
/// </summary>
public static class ThemedDialog
{
    public static MessageBoxResult Show(string text, string caption,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None,
        Window? owner = null)
    {
        var app = Application.Current;
        Brush Res(string key) => (Brush)app.FindResource(key);

        var result = buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel
            ? MessageBoxResult.No
            : MessageBoxResult.Cancel;
        if (buttons == MessageBoxButton.OK) result = MessageBoxResult.OK;

        var dlg = new Window
        {
            Title = caption,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner == null,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Background = Res("EditorWindowBg"),
            MinWidth = 300,
        };
        if (owner != null && owner.IsVisible) dlg.Owner = owner;
        else dlg.Topmost = true; // e.g. jump-list "Clear Cache" has no window of its own
        dlg.SourceInitialized += (_, _) => DwmHelper.ApplyTitleBarTheme(dlg);

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

        // Icon + message
        var body = new DockPanel { Margin = new Thickness(0, 0, 0, 20) };
        (string? glyph, Color? color) = image switch
        {
            MessageBoxImage.Error => ("", Color.FromRgb(0xE8, 0x4A, 0x4A)),
            MessageBoxImage.Warning => ("", Color.FromRgb(0xE8, 0xA3, 0x1C)),
            MessageBoxImage.Question => ("", null),
            MessageBoxImage.Information => ("", null),
            _ => ((string?)null, (Color?)null),
        };
        if (glyph != null)
        {
            var icon = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 24,
                Margin = new Thickness(0, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Top,
                // Accent for info/question; fixed red/amber for error/warning (accent in high contrast)
                Foreground = color is { } c && !SystemParameters.HighContrast ? new SolidColorBrush(c) : Res("EditorAccent"),
            };
            DockPanel.SetDock(icon, Dock.Left);
            body.Children.Add(icon);
        }
        body.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Foreground = Res("ItemForeground"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        root.Children.Add(body);

        // Buttons
        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Button? first = null;
        void AddButton(string label, MessageBoxResult value, bool isDefault, bool isCancel)
        {
            var b = new Button
            {
                Content = label,
                MinWidth = 80,
                Height = 30,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(buttonRow.Children.Count > 0 ? 8 : 0, 0, 0, 0),
                Style = (Style)app.FindResource("EditorButton"),
                IsDefault = isDefault,
                IsCancel = isCancel,
            };
            b.Click += (_, _) => { result = value; dlg.Close(); };
            buttonRow.Children.Add(b);
            first ??= b;
        }

        switch (buttons)
        {
            case MessageBoxButton.YesNo:
                AddButton(Strings.Get("Yes"), MessageBoxResult.Yes, true, false);
                AddButton(Strings.Get("No"), MessageBoxResult.No, false, true);
                break;
            case MessageBoxButton.YesNoCancel:
                AddButton(Strings.Get("Yes"), MessageBoxResult.Yes, true, false);
                AddButton(Strings.Get("No"), MessageBoxResult.No, false, false);
                AddButton(Strings.Get("Cancel"), MessageBoxResult.Cancel, false, true);
                break;
            case MessageBoxButton.OKCancel:
                AddButton(Strings.Get("OK"), MessageBoxResult.OK, true, false);
                AddButton(Strings.Get("Cancel"), MessageBoxResult.Cancel, false, true);
                break;
            default:
                AddButton(Strings.Get("OK"), MessageBoxResult.OK, true, true);
                break;
        }
        root.Children.Add(buttonRow);
        dlg.Content = root;

        dlg.Loaded += (_, _) => { dlg.Activate(); first?.Focus(); };
        dlg.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) dlg.Close(); // keeps the No/Cancel/OK default result
        };

        dlg.ShowDialog();
        return result;
    }
}
