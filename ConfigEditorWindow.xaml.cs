using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using JumpListLauncher.Helpers;
using JumpListLauncher.Models;
using Microsoft.Win32;

namespace JumpListLauncher;

public class AppEditItem : INotifyPropertyChanged
{
    private string _name = "";
    private string _path = "";
    private string? _autoName;
    private BitmapSource? _icon;

    /// <summary>Custom name saved to config; empty means "use the name Windows reports".</summary>
    public string Name
    {
        get => _name;
        set { _name = value; Notify(nameof(Name)); NotifyDisplay(); }
    }

    public string Path
    {
        get => _path;
        set
        {
            _path = value;
            _autoName = null;
            _icon = null;
            Notify(nameof(Path)); Notify(nameof(Icon)); NotifyDisplay();
        }
    }

    /// <summary>Name resolved from the file (e.g. "메모장" for notepad.exe), filled in the background.</summary>
    public string? AutoName
    {
        get => _autoName;
        set { _autoName = value; NotifyDisplay(); }
    }

    public BitmapSource? Icon
    {
        get => _icon;
        set { _icon = value; Notify(nameof(Icon)); }
    }

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name)) return Name;
            if (!string.IsNullOrWhiteSpace(AutoName)) return AutoName!;
            var cached = IconExtractor.GetCachedDisplayName(Path);
            if (!string.IsNullOrWhiteSpace(cached)) return cached!;
            var fileName = System.IO.Path.GetFileNameWithoutExtension(Path);
            return string.IsNullOrEmpty(fileName) ? Path : fileName;
        }
    }

    /// <summary>Path shown under the name; hidden when it would just repeat the name (e.g. URLs).</summary>
    public string SecondaryText => string.Equals(DisplayName, Path, StringComparison.OrdinalIgnoreCase) ? "" : Path;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void NotifyDisplay() { Notify(nameof(DisplayName)); Notify(nameof(SecondaryText)); }
}

public partial class ConfigEditorWindow : Window
{
    private readonly string _configPath;
    private readonly ObservableCollection<AppEditItem> _items = new();
    private AppConfig _config = new();
    private int _selectedGroupIndex;

    public bool WasSaved { get; private set; }

    private readonly int _initialGroupIndex;

    /// <param name="initialGroupIndex">Group to select when the editor opens (e.g. the popup's current tab).</param>
    public ConfigEditorWindow(string configPath, int initialGroupIndex = 0)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DwmHelper.ApplyTitleBarTheme(this);
        HoverHelper.Attach(this);
        _configPath = configPath;
        _initialGroupIndex = initialGroupIndex;

        // Apply theme background
        Background = (System.Windows.Media.Brush)FindResource("EditorWindowBg");

        // Apply localized strings
        Title = Strings.Get("EditorTitle");
        LblGroupName.Text = Strings.Get("GroupName");
        LblAppList.Text = Strings.Get("AppList");
        TxtDropHint.Text = Strings.Get("DropHint");
        BtnAdd.Content = IconContent("\uE710", Strings.Get("Add"), "\uE70D");
        BtnRemove.Content = IconContent("\uE74D", Strings.Get("Remove"));
        BtnRename.Content = IconContent("\uE8AC", Strings.Get("Rename"));
        BtnSave.Content = Strings.Get("Save");
        BtnCancel.Content = Strings.Get("Cancel");
        BtnAddGroup.Content = IconContent("\uE710", Strings.Get("AddGroup"));
        BtnDeleteGroup.Content = IconContent("\uE74D", Strings.Get("DeleteGroup"));
        MnuAddApp.Header = Strings.Get("AddMenuApp");
        MnuAddFolder.Header = Strings.Get("AddMenuFolder");
        MnuAddUrl.Header = Strings.Get("AddMenuUrl");
        MnuRename.Header = Strings.Get("Rename");
        MnuUp.Header = Strings.Get("MoveUp");
        MnuDown.Header = Strings.Get("MoveDown");
        MnuRemove.Header = Strings.Get("Remove");
        LblGroup.Text = Strings.Get("GroupLabel");

        AppListBox.ItemsSource = _items;
        UpdateItemButtons();

        LoadConfig();
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                _config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                _config.MigrateIfNeeded();
            }
            else
            {
                _config = new AppConfig();
                _config.MigrateIfNeeded();
            }

            // Populate group combo
            RefreshGroupCombo();
            if (_config.Groups.Count > 0)
            {
                CmbGroup.SelectedIndex = Math.Clamp(_initialGroupIndex, 0, _config.Groups.Count - 1);
            }
        }
        catch (Exception ex)
        {
            ThemedDialog.Show($"{Strings.Get("ErrorLoad")}: {ex.Message}", Strings.Get("Error"),
                MessageBoxButton.OK, MessageBoxImage.Error, this);
        }
    }

    private void RefreshGroupCombo()
    {
        var items = new ObservableCollection<string>();
        foreach (var g in _config.Groups)
            items.Add(g.GroupName);
        CmbGroup.ItemsSource = items;
    }

    private void CmbGroup_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CmbGroup.SelectedIndex < 0) return;

        // Save current group before switching
        if (_selectedGroupIndex >= 0 && _selectedGroupIndex < _config.Groups.Count
            && _selectedGroupIndex != CmbGroup.SelectedIndex)
        {
            SaveCurrentGroupApps();
        }

        _selectedGroupIndex = CmbGroup.SelectedIndex;
        LoadGroupApps(_selectedGroupIndex);
    }

    /// <summary>
    /// WPF's ComboBox only reacts to the wheel when it has keyboard focus, which it doesn't when the
    /// editor first opens. Step through groups whenever the mouse is over it (dropdown closed).
    /// </summary>
    private void CmbGroup_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (CmbGroup.IsDropDownOpen || CmbGroup.Items.Count == 0) return;
        var next = CmbGroup.SelectedIndex + (e.Delta > 0 ? -1 : 1);
        CmbGroup.SelectedIndex = Math.Clamp(next, 0, CmbGroup.Items.Count - 1);
        e.Handled = true;
    }

    private void LoadGroupApps(int index)
    {
        if (index < 0 || index >= _config.Groups.Count) return;
        var group = _config.Groups[index];
        GroupNameBox.Text = group.GroupName;
        _items.Clear();
        foreach (var app in group.Apps)
        {
            _items.Add(new AppEditItem { Name = app.Name, Path = app.Path, Icon = IconExtractor.GetCachedIcon(app.Path) });
        }
        LoadDetailsInBackground(_items.ToList());
    }

    private void AddItem(AppEditItem item, int index = -1)
    {
        if (index < 0 || index > _items.Count) _items.Add(item);
        else _items.Insert(index, item);
        AppListBox.SelectedItem = item;
        AppListBox.ScrollIntoView(item);
        LoadDetailsInBackground(new[] { item });
    }

    /// <summary>Resolve display names and icons off the UI thread, like the popup does.</summary>
    private void LoadDetailsInBackground(IReadOnlyList<AppEditItem> items)
    {
        var work = items.Select(i => (Item: i, Path: i.Path)).ToList();
        Task.Run(() =>
        {
            foreach (var (item, path) in work)
            {
                try
                {
                    var name = IconExtractor.GetDisplayName(path);
                    var icon = IconExtractor.ExtractIcon(path);
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (item.Path != path) return; // path changed meanwhile
                        item.AutoName = name;
                        item.Icon = icon;
                    });
                }
                catch { }
            }
        });
    }

    private void GroupNameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_selectedGroupIndex >= 0 && _selectedGroupIndex < _config.Groups.Count)
        {
            _config.Groups[_selectedGroupIndex].GroupName = GroupNameBox.Text.Trim();
        }
    }

    private void SaveCurrentGroupApps()
    {
        if (_selectedGroupIndex < 0 || _selectedGroupIndex >= _config.Groups.Count) return;
        var group = _config.Groups[_selectedGroupIndex];
        group.GroupName = GroupNameBox.Text.Trim();
        group.Apps.Clear();
        foreach (var item in _items)
        {
            group.Apps.Add(new AppEntry { Name = item.Name, Path = item.Path });
        }
    }

    private void AddGroup_Click(object sender, RoutedEventArgs e)
    {
        // Save current group first
        SaveCurrentGroupApps();

        var newGroup = new AppGroup
        {
            GroupName = $"Group {_config.Groups.Count + 1}"
        };
        _config.Groups.Add(newGroup);
        RefreshGroupCombo();
        CmbGroup.SelectedIndex = _config.Groups.Count - 1;
    }

    private void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_config.Groups.Count <= 1)
        {
            // Can't delete the last group
            return;
        }

        var groupName = _config.Groups[_selectedGroupIndex].GroupName;
        var msg = string.Format(Strings.Get("ConfirmDeleteGroup"), groupName);
        var result = ThemedDialog.Show(msg, Strings.Get("Confirm"),
            MessageBoxButton.YesNo, MessageBoxImage.Question, this);

        if (result == MessageBoxResult.Yes)
        {
            _config.Groups.RemoveAt(_selectedGroupIndex);
            RefreshGroupCombo();
            CmbGroup.SelectedIndex = Math.Min(_selectedGroupIndex, _config.Groups.Count - 1);
        }
    }

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Strings.Get("EditorTitle"),
            Filter = "Executables (*.exe)|*.exe|Shortcuts (*.lnk)|*.lnk|All (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            AddItem(new AppEditItem
            {
                Name = "",
                Path = dialog.FileName
            });
        }
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = Strings.Get("AddFolder"),
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            AddItem(new AppEditItem
            {
                Name = "",
                Path = dialog.SelectedPath
            });
        }
    }

    private void AddUrl_Click(object sender, RoutedEventArgs e)
    {
        var inputWindow = new Window
        {
            Title = Strings.Get("UrlInputTitle"),
            Width = 420, Height = 190,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)FindResource("EditorWindowBg")
        };
        inputWindow.SourceInitialized += (_, _) => DwmHelper.ApplyTitleBarTheme(inputWindow);

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        var label = new System.Windows.Controls.TextBlock
        {
            Text = Strings.Get("UrlInputPrompt"),
            Foreground = (System.Windows.Media.Brush)FindResource("HeaderForeground"),
            FontSize = 13, Margin = new Thickness(0, 0, 0, 8)
        };
        var textBox = new System.Windows.Controls.TextBox
        {
            Style = (Style)FindResource("EditorTextBox"),
            Text = "https://"
        };
        var btnOk = new System.Windows.Controls.Button
        {
            Content = "OK", Width = 80, Height = 30, Padding = new Thickness(0),
            Style = (Style)FindResource("EditorButton"),
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        btnOk.Click += (s, ev) => { inputWindow.DialogResult = true; inputWindow.Close(); };
        textBox.KeyDown += (s, ev) =>
        {
            if (ev.Key == System.Windows.Input.Key.Enter)
            {
                inputWindow.DialogResult = true;
                inputWindow.Close();
            }
            else if (ev.Key == System.Windows.Input.Key.Escape)
            {
                inputWindow.DialogResult = false;
                inputWindow.Close();
            }
        };
        inputWindow.KeyDown += (s, ev) =>
        {
            if (ev.Key == System.Windows.Input.Key.Escape)
            {
                inputWindow.DialogResult = false;
                inputWindow.Close();
            }
        };

        panel.Children.Add(label);
        panel.Children.Add(textBox);
        panel.Children.Add(btnOk);
        inputWindow.Content = panel;

        if (inputWindow.ShowDialog() == true && !string.IsNullOrWhiteSpace(textBox.Text))
        {
            var url = textBox.Text.Trim();
            AddItem(new AppEditItem
            {
                Name = url,
                Path = url
            });
        }
    }

    private void RenameApp_Click(object sender, RoutedEventArgs e)
    {
        if (AppListBox.SelectedItem is AppEditItem item)
            ShowRenameDialog(item);
    }

    private void AppListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (AppListBox.SelectedItem is AppEditItem item)
            ShowRenameDialog(item);
    }

    private void ShowRenameDialog(AppEditItem item)
    {
        var currentName = item.DisplayName;

        var inputWindow = new Window
        {
            Title = Strings.Get("RenameTitle"),
            Width = 420, Height = 190,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)FindResource("EditorWindowBg")
        };
        inputWindow.SourceInitialized += (_, _) => DwmHelper.ApplyTitleBarTheme(inputWindow);

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        var label = new System.Windows.Controls.TextBlock
        {
            Text = Strings.Get("RenamePrompt"),
            Foreground = (System.Windows.Media.Brush)FindResource("HeaderForeground"),
            FontSize = 13, Margin = new Thickness(0, 0, 0, 8)
        };
        var textBox = new System.Windows.Controls.TextBox
        {
            Style = (Style)FindResource("EditorTextBox"),
            Text = currentName
        };
        textBox.SelectAll();
        var btnOk = new System.Windows.Controls.Button
        {
            Content = "OK", Width = 80, Height = 30, Padding = new Thickness(0),
            Style = (Style)FindResource("EditorButton"),
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        btnOk.Click += (s, ev) => { inputWindow.DialogResult = true; inputWindow.Close(); };
        textBox.KeyDown += (s, ev) =>
        {
            if (ev.Key == System.Windows.Input.Key.Enter)
            {
                inputWindow.DialogResult = true;
                inputWindow.Close();
            }
            else if (ev.Key == System.Windows.Input.Key.Escape)
            {
                inputWindow.DialogResult = false;
                inputWindow.Close();
            }
        };
        inputWindow.KeyDown += (s, ev) =>
        {
            if (ev.Key == System.Windows.Input.Key.Escape)
            {
                inputWindow.DialogResult = false;
                inputWindow.Close();
            }
        };

        panel.Children.Add(label);
        panel.Children.Add(textBox);
        panel.Children.Add(btnOk);
        inputWindow.Content = panel;

        if (inputWindow.ShowDialog() == true)
        {
            var newName = textBox.Text.Trim();
            // Unchanged → keep as is, so an automatic name isn't frozen into a custom one.
            // Empty → back to the automatic name.
            if (newName != currentName)
                item.Name = newName;
        }
    }

    private void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (AppListBox.SelectedItem is AppEditItem item)
        {
            _items.Remove(item);
        }
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        var index = AppListBox.SelectedIndex;
        if (index > 0)
        {
            _items.Move(index, index - 1);
            AppListBox.SelectedIndex = index - 1;
        }
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        var index = AppListBox.SelectedIndex;
        if (index >= 0 && index < _items.Count - 1)
        {
            _items.Move(index, index + 1);
            AppListBox.SelectedIndex = index + 1;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveCurrentGroupApps();

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            var json = JsonSerializer.Serialize(_config, options);
            File.WriteAllText(_configPath, json);

            WasSaved = true;
            Close();
        }
        catch (Exception ex)
        {
            ThemedDialog.Show($"{Strings.Get("ErrorLoad")}: {ex.Message}", Strings.Get("Error"),
                MessageBoxButton.OK, MessageBoxImage.Error, this);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        EndDragSession();
        base.OnClosed(e);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == System.Windows.Input.Key.Escape)
            Close();
    }

    // === Toolbar helpers ===

    private static object IconContent(string glyph, string text, string? trailingGlyph = null)
    {
        var iconFont = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        var panel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = glyph, FontFamily = iconFont, FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 7, 0)
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        if (trailingGlyph != null)
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = trailingGlyph, FontFamily = iconFont, FontSize = 9,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 2, 0, 0)
            });
        }
        return panel;
    }

    private void AddMenu_Click(object sender, RoutedEventArgs e)
    {
        AddMenu.PlacementTarget = BtnAdd;
        AddMenu.IsOpen = true;
    }

    private void UpdateItemButtons()
    {
        bool hasSelection = AppListBox.SelectedItem != null;
        BtnRename.IsEnabled = hasSelection;
        BtnRemove.IsEnabled = hasSelection;
    }

    private void AppListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => UpdateItemButtons();

    private void AppListBox_ContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        // Mouse right-click targets the item under the mouse (no menu on empty space);
        // the keyboard menu key (CursorLeft == -1) uses the current selection.
        if (e.CursorLeft >= 0)
        {
            var item = ItemAt(Mouse.GetPosition(AppListBox));
            if (item == null) { e.Handled = true; return; }
            AppListBox.SelectedItem = item;
        }
        if (AppListBox.SelectedItem == null) { e.Handled = true; return; }

        int index = AppListBox.SelectedIndex;
        MnuUp.IsEnabled = index > 0;
        MnuDown.IsEnabled = index < _items.Count - 1;
    }

    private void AppListBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (AppListBox.SelectedItem is not AppEditItem item) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool alt = Keyboard.Modifiers == ModifierKeys.Alt;

        if (key == Key.Delete) { RemoveApp_Click(sender, e); e.Handled = true; }
        else if (key == Key.F2) { ShowRenameDialog(item); e.Handled = true; }
        else if (alt && key == Key.Up) { MoveUp_Click(sender, e); e.Handled = true; }
        else if (alt && key == Key.Down) { MoveDown_Click(sender, e); e.Handled = true; }
    }

    // === Drag & drop: files from Explorer to add, items within the list to reorder ===

    private const string ItemDragFormat = "Groupa.AppEditItem";
    private Point _dragStart;
    private AppEditItem? _dragCandidate;

    private AppEditItem? ItemAt(Point pos)
    {
        var hit = AppListBox.InputHitTest(pos) as DependencyObject;
        while (hit != null && hit is not System.Windows.Controls.ListBoxItem)
            hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
        return hit is System.Windows.Controls.ListBoxItem lbi
            ? AppListBox.ItemContainerGenerator.ItemFromContainer(lbi) as AppEditItem
            : null;
    }

    private void AppListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(AppListBox);
        _dragCandidate = ItemAt(_dragStart);
    }

    private void AppListBox_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragCandidate == null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(AppListBox);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var item = _dragCandidate;
        _dragCandidate = null;
        AppListBox.SelectedItem = item;
        DragDrop.DoDragDrop(AppListBox, new DataObject(ItemDragFormat, item), DragDropEffects.Move);
        EndDragSession();
    }

    private void AppListBox_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(ItemDragFormat))
        {
            e.Effects = DragDropEffects.Move;
            if (e.Data.GetData(ItemDragFormat) is AppEditItem dragged)
            {
                TouchDragSession(isFiles: false, dragged);
                ReorderDraggedTo(e.GetPosition(AppListBox), dragged);
            }
        }
        else if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            TouchDragSession(isFiles: true, null);
            ShowDropLine(DropIndexAt(e.GetPosition(AppListBox)));
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    /// <summary>Live reorder: move the dragged item to the slot under the cursor.</summary>
    private void ReorderDraggedTo(Point pos, AppEditItem dragged)
    {
        if (ItemAt(pos) is not { } target || ReferenceEquals(dragged, target)) return;
        int from = _items.IndexOf(dragged), to = _items.IndexOf(target);
        if (from < 0 || to < 0) return;
        _items.Move(from, to);
        AppListBox.SelectedItem = dragged;
    }

    private void AppListBox_Drop(object sender, DragEventArgs e)
    {
        EndDragSession();
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) && e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths)
        {
            int index = DropIndexAt(e.GetPosition(AppListBox));
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                AddItem(new AppEditItem { Name = "", Path = path }, index++);
            }
            Activate();
        }
        e.Handled = true;
    }

    private void AppListBox_DragLeave(object sender, DragEventArgs e)
    {
        // Also fires when crossing between child elements, but DragEnter/DragOver right after re-show it.
        // Checking the position here isn't reliable: a fast exit can still report a point inside.
        HideDropLine();
        LeaveDragSession();
    }

    /// <summary>Insertion index for a drop at <paramref name="pos"/>: before an item's upper half, after its lower half.</summary>
    private int DropIndexAt(Point pos)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (AppListBox.ItemContainerGenerator.ContainerFromIndex(i) is not System.Windows.Controls.ListBoxItem c
                || !c.IsVisible) continue;
            var top = c.TranslatePoint(new Point(0, 0), AppListBox).Y;
            if (pos.Y < top + c.ActualHeight / 2) return i;
        }
        return _items.Count;
    }

    private void ShowDropLine(int index)
    {
        double y;
        if (_items.Count == 0)
        {
            y = 4;
        }
        else if (index < _items.Count && AppListBox.ItemContainerGenerator.ContainerFromIndex(index) is System.Windows.Controls.ListBoxItem next)
        {
            y = next.TranslatePoint(new Point(0, 0), AppListBox).Y;
        }
        else if (AppListBox.ItemContainerGenerator.ContainerFromIndex(_items.Count - 1) is System.Windows.Controls.ListBoxItem last)
        {
            y = last.TranslatePoint(new Point(0, last.ActualHeight), AppListBox).Y;
        }
        else return;

        y = Math.Clamp(y - 1, 1, Math.Max(1, AppListBox.ActualHeight - 3));
        System.Windows.Controls.Canvas.SetLeft(DropLine, 6);
        System.Windows.Controls.Canvas.SetTop(DropLine, y);
        DropLine.Width = Math.Max(0, AppListBox.ActualWidth - 12);
        DropLine.Visibility = Visibility.Visible;
    }

    private void HideDropLine() => DropLine.Visibility = Visibility.Collapsed;
}
