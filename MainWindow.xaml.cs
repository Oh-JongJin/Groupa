using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JumpListLauncher.Models;
using JumpListLauncher.Helpers;

namespace JumpListLauncher;

public class AppDisplayItem : INotifyPropertyChanged
{
    private string _name = "";
    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }
    public string Path { get; set; } = "";
    public bool HasCustomName { get; set; }

    private BitmapSource? _icon;
    public BitmapSource? Icon
    {
        get => _icon;
        set { _icon = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class GroupTabItem : INotifyPropertyChanged
{
    public string GroupName { get; set; } = "";
    public int Index { get; set; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class MainWindow : Window
{
    private readonly string _configPath;
    private ObservableCollection<AppDisplayItem>? _displayItems;
    private AppConfig? _config;
    private int _selectedGroupIndex;
    private List<GroupTabItem>? _groupTabs;
    private bool _initialHeightLocked;
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();
        _configPath = AppPaths.ConfigPath;

        // Apply localized strings
        BtnSettings.Content = Strings.Get("Settings");
        BtnClose.Content = Strings.Get("Close");

        LoadConfigFast();

        BtnPreferences.Content = Strings.Get("Preferences");

        Loaded += MainWindow_Loaded;
        Deactivated += MainWindow_Deactivated;
        Closing += (s, e) =>
        {
            _isClosing = true;
            Deactivated -= MainWindow_Deactivated;
        };
        SizeChanged += MainWindow_SizeChanged;
    }

    // Fixed anchor: the bottom edge Y position of the popup (never changes)
    private double _bottomAnchorY;
    private double _iconCenterX;
    private bool _anchored;

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_anchored)
        {
            // Keep bottom fixed, grow upward
            Top = _bottomAnchorY - ActualHeight;
            Left = _iconCenterX - (ActualWidth / 2.0);

            // Clamp left to current monitor work area
            var workArea = WindowPositioner.GetCurrentMonitorWorkArea();
            var src = PresentationSource.FromVisual(this);
            double dpiX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double workLeft = workArea.Left / dpiX;
            double workWidth = workArea.Width / dpiX;
            if (Left < workLeft + 8) Left = workLeft + 8;
            if (Left + ActualWidth > workLeft + workWidth - 8)
                Left = workLeft + workWidth - ActualWidth - 8;
        }
    }

    private void LoadConfigFast()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                var defaultConfig = new AppConfig();
                defaultConfig.Groups.Add(new AppGroup
                {
                    GroupName = "Groupa",
                    Apps = new[]
                    {
                        "C:\\Windows\\System32\\notepad.exe",
                        "calc.exe",
                        "C:\\Windows\\explorer.exe"
                    }.Select(p => new AppEntry { Name = "", Path = p }).ToList()
                });
                SaveConfig(defaultConfig);
            }

            var configJson = File.ReadAllText(_configPath);
            _config = JsonSerializer.Deserialize<AppConfig>(configJson);

            if (_config != null)
            {
                _config.MigrateIfNeeded();

                // Build group tabs
                _groupTabs = new List<GroupTabItem>();
                for (int i = 0; i < _config.Groups.Count; i++)
                {
                    _groupTabs.Add(new GroupTabItem
                    {
                        GroupName = _config.Groups[i].GroupName,
                        Index = i,
                        IsSelected = i == 0
                    });
                }

                // Show tab bar only when multiple groups, hide header text to avoid duplication
                bool multiGroup = _config.Groups.Count > 1;
                GroupTabs.Visibility = multiGroup ? Visibility.Visible : Visibility.Collapsed;
                HeaderText.Visibility = multiGroup ? Visibility.Collapsed : Visibility.Visible;
                GroupTabs.ItemsSource = _groupTabs;

                _selectedGroupIndex = 0;
                LoadGroup(0);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{Strings.Get("ErrorLoad")}: {ex.Message}", Strings.Get("Error"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadGroup(int index)
    {
        if (_config == null || index < 0 || index >= _config.Groups.Count) return;

        _selectedGroupIndex = index;
        var group = _config.Groups[index];
        HeaderText.Text = group.GroupName;

        _displayItems = new ObservableCollection<AppDisplayItem>();
        foreach (var app in group.Apps)
        {
            bool hasCustom = !string.IsNullOrWhiteSpace(app.Name);
            var cachedName = hasCustom ? app.Name : IconExtractor.GetCachedDisplayName(app.Path);
            var fastName = !string.IsNullOrEmpty(cachedName)
                ? cachedName
                : (hasCustom ? app.Name : (System.IO.Path.GetFileNameWithoutExtension(app.Path) is { Length: > 0 } fn ? fn : app.Path));

            var cachedIcon = IconExtractor.GetCachedIcon(app.Path);

            _displayItems.Add(new AppDisplayItem
            {
                Name = fastName,
                Path = app.Path,
                HasCustomName = hasCustom,
                Icon = cachedIcon
            });
        }

        AppList.ItemsSource = _displayItems;

        // Update tab selection
        if (_groupTabs != null)
        {
            foreach (var tab in _groupTabs)
                tab.IsSelected = tab.Index == index;
        }
    }

    private void GroupTab_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is GroupTabItem tab)
        {
            LoadGroup(tab.Index);
            StartBackgroundDetailsLoading();
        }
    }

    private void StartBackgroundDetailsLoading()
    {
        if (_displayItems == null) return;
        var itemsSnapshot = _displayItems.ToList();
        Task.Run(() =>
        {
            foreach (var item in itemsSnapshot)
            {
                try
                {
                    // Resolve localized display name in background (0ms UI thread impact)
                    if (!item.HasCustomName)
                    {
                        var realName = IconExtractor.GetDisplayName(item.Path);
                        if (!string.IsNullOrWhiteSpace(realName) && realName != item.Name)
                        {
                            Dispatcher.BeginInvoke(() => item.Name = realName);
                        }
                    }

                    var bmp = IconExtractor.ExtractIcon(item.Path);
                    bmp?.Freeze();
                    Dispatcher.BeginInvoke(() => item.Icon = bmp);
                }
                catch { }
            }
        });
    }

    private void SaveConfig(AppConfig config)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        File.WriteAllText(_configPath, JsonSerializer.Serialize(config, options));
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        DwmHelper.EnableMica(this);
        UpdateLayout();

        var (left, top) = WindowPositioner.CalculatePosition(this);
        Left = left;
        Top = top;

        // Lock the height to the first group's rendered size, then switch to Width-only auto sizing
        if (!_initialHeightLocked)
        {
            Height = ActualHeight;
            SizeToContent = SizeToContent.Width;
            _initialHeightLocked = true;
        }

        // Store the fixed bottom anchor = top + height (this never changes)
        _bottomAnchorY = top + ActualHeight;
        _iconCenterX = left + (ActualWidth / 2.0);
        _anchored = true;

        Activate();

        // Load details and icons asynchronously in background
        StartBackgroundDetailsLoading();
    }

    public void SafeClose()
    {
        if (_isClosing) return;
        _isClosing = true;

        Deactivated -= MainWindow_Deactivated;

        try
        {
            Close();
        }
        catch { }

        if (!App.HotkeyMode)
        {
            Application.Current.Shutdown();
        }
    }

    private void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        SafeClose();
    }

    private void AppItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is AppDisplayItem item)
        {
            try
            {
                Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
                SafeClose();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{Strings.Get("ErrorLaunch")}: {ex.Message}", Strings.Get("Error"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        Deactivated -= MainWindow_Deactivated;
        _isClosing = true;
        try { Close(); } catch { }

        var editor = new ConfigEditorWindow(_configPath);
        editor.ShowDialog();

        if (!App.HotkeyMode)
            Application.Current.Shutdown();
    }

    private void Preferences_Click(object sender, RoutedEventArgs e)
    {
        Deactivated -= MainWindow_Deactivated;
        _isClosing = true;
        try { Close(); } catch { }

        var prefs = new PreferencesWindow();
        prefs.ShowDialog();

        if (Application.Current is App app)
        {
            app.ReloadHotkey();
        }

        if (!App.HotkeyMode)
            Application.Current.Shutdown();
    }

    private void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is AppDisplayItem item)
        {
            try
            {
                _displayItems?.Remove(item);
                SaveConfigOrder();

                // If on first group, recalculate the locked height
                if (_selectedGroupIndex == 0 && _initialHeightLocked)
                {
                    SizeToContent = SizeToContent.WidthAndHeight;
                    UpdateLayout();
                    Height = ActualHeight;
                    SizeToContent = SizeToContent.Width;
                }
            }
            catch { }
        }
    }

    // === Drag & Drop Reorder ===
    private Point _dragStartPoint;
    private AppDisplayItem? _draggedItem;

    private void AppList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);

        // Find the clicked AppDisplayItem
        if (e.OriginalSource is DependencyObject source)
        {
            var listBoxItem = FindAncestor<ListBoxItem>(source);
            if (listBoxItem != null)
                _draggedItem = listBoxItem.DataContext as AppDisplayItem;
        }
    }

    private void AppList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedItem == null)
            return;

        var pos = e.GetPosition(null);
        var diff = _dragStartPoint - pos;

        // Only start drag if moved enough (avoid accidental drags)
        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            // Check if we're over the delete button — don't start drag
            if (e.OriginalSource is DependencyObject src)
            {
                var button = FindAncestor<Button>(src);
                if (button != null && button.Content?.ToString() == "✕")
                    return;
            }

            var data = new DataObject("AppDisplayItem", _draggedItem);
            DragDrop.DoDragDrop(AppList, data, DragDropEffects.Move);
            _draggedItem = null;
        }
    }

    private void AppList_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("AppDisplayItem") || _displayItems == null)
            return;

        e.Effects = DragDropEffects.Move;
        e.Handled = true;

        var pos = e.GetPosition(AppList);
        int insertIndex = GetInsertionIndex(pos);

        // Position the indicator line
        if (insertIndex >= 0)
        {
            double y = GetInsertionY(insertIndex);
            DropIndicator.Margin = new Thickness(12, y, 12, 0);
            DropIndicator.Visibility = Visibility.Visible;
        }
    }

    private void AppList_DragLeave(object sender, DragEventArgs e)
    {
        DropIndicator.Visibility = Visibility.Collapsed;
    }

    private void AppList_Drop(object sender, DragEventArgs e)
    {
        DropIndicator.Visibility = Visibility.Collapsed;

        if (!e.Data.GetDataPresent("AppDisplayItem") || _displayItems == null)
            return;

        var droppedItem = e.Data.GetData("AppDisplayItem") as AppDisplayItem;
        if (droppedItem == null) return;

        var pos = e.GetPosition(AppList);
        int newIndex = GetInsertionIndex(pos);
        int oldIndex = _displayItems.IndexOf(droppedItem);

        if (oldIndex < 0 || newIndex < 0) return;

        // Adjust index if moving downward
        if (newIndex > oldIndex) newIndex--;
        if (newIndex == oldIndex) return;

        _displayItems.Move(oldIndex, newIndex);
        SaveConfigOrder();
    }

    private int GetInsertionIndex(Point position)
    {
        if (_displayItems == null || _displayItems.Count == 0) return 0;

        for (int i = 0; i < _displayItems.Count; i++)
        {
            var container = AppList.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem;
            if (container == null) continue;

            var topLeft = container.TranslatePoint(new Point(0, 0), AppList);
            double midY = topLeft.Y + container.ActualHeight / 2.0;

            if (position.Y < midY)
                return i;
        }
        return _displayItems.Count;
    }

    private double GetInsertionY(int index)
    {
        if (_displayItems == null || _displayItems.Count == 0) return 0;

        if (index < _displayItems.Count)
        {
            var container = AppList.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
            if (container != null)
            {
                var topLeft = container.TranslatePoint(new Point(0, 0), AppList);
                return topLeft.Y;
            }
        }

        // After last item
        var lastContainer = AppList.ItemContainerGenerator.ContainerFromIndex(_displayItems.Count - 1) as ListBoxItem;
        if (lastContainer != null)
        {
            var topLeft = lastContainer.TranslatePoint(new Point(0, 0), AppList);
            return topLeft.Y + lastContainer.ActualHeight;
        }
        return 0;
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T found) return found;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void SaveConfigOrder()
    {
        if (_displayItems == null || _config == null) return;
        if (_selectedGroupIndex < 0 || _selectedGroupIndex >= _config.Groups.Count) return;
        try
        {
            var group = _config.Groups[_selectedGroupIndex];

            // Rebuild apps list from current display order
            var newApps = new List<AppEntry>();
            foreach (var displayItem in _displayItems)
            {
                var original = group.Apps.FirstOrDefault(a => a.Path == displayItem.Path);
                newApps.Add(original ?? new AppEntry { Name = "", Path = displayItem.Path });
            }
            group.Apps = newApps;

            SaveConfig(_config);
        }
        catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        SafeClose();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            SafeClose();
        }
    }
}