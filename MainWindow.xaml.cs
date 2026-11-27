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

    private readonly bool _suppressLoadErrors;

    /// <summary>True if the config failed to load (only tracked for pre-warmed windows).</summary>
    public bool LoadFailed { get; private set; }

    public MainWindow() : this(prewarm: false) { }

    /// <param name="prewarm">
    /// Built ahead of time while idle (background mode) so the next show is instant.
    /// Load errors are not shown here; the caller discards the window and builds a fresh one on show.
    /// </param>
    public MainWindow(bool prewarm)
    {
        _suppressLoadErrors = prewarm;
        InitializeComponent();
        _configPath = AppPaths.ConfigPath;

        // Apply localized strings
        BtnSettings.Content = Strings.Get("Settings");
        BtnClose.Content = Strings.Get("Close");

        LoadConfigFast();

        BtnPreferences.Content = Strings.Get("Preferences");
        EmptyText.Text = Strings.Get("EmptyGroup");
        BtnEmptyAdd.Content = Strings.Get("AddApp");
        BtnUndo.Content = Strings.Get("Undo");

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
            LoadFailed = true;
            if (_suppressLoadErrors) return;
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
        UpdateEmptyState();
        HideUndo(); // an undo only applies to the group it was made in

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

    private bool _handlePrepared;

    /// <summary>
    /// Create the HWND and apply the backdrop without showing the window, so background mode can do it
    /// ahead of time while idle. Layout can't be done here (WPF skips layout for hidden windows), so
    /// sizing/positioning still happens in Loaded, inside Show(). Idempotent.
    /// </summary>
    public void PrepareHandle()
    {
        if (_handlePrepared) return;
        _handlePrepared = true;

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        DwmHelper.EnableMica(this);
    }

    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MINIMIZE = 0xF020;

    /// <summary>
    /// The popup is shown in the taskbar (so the pinned button gets the "running" underline). Clicking
    /// the taskbar button of the active window then asks it to minimize instead of relaunching the exe,
    /// so treat that as the toggle-close. (ResizeMode=CanMinimize is required for the taskbar to send it.)
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_MINIMIZE)
        {
            handled = true;
            // No relaunch/signal follows this click, so the deactivation must not arm the signal swallow
            _closingByTaskbarToggle = true;
            Dispatcher.BeginInvoke(SafeClose);
        }
        return IntPtr.Zero;
    }

    public void ShowPopupWindow()
    {
        PrepareHandle();
        Show();
        Activate();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        PrepareHandle();
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

        // No Activate() here: with a pre-created handle, Loaded fires in the middle of Show() where
        // Activate() throws. ShowPopupWindow() activates right after Show() returns.

        // Load details and icons asynchronously in background
        StartBackgroundDetailsLoading();
    }

    /// <summary>True if the popup closed because the user clicked somewhere on the taskbar/tray.</summary>
    public bool DeactivatedOnTaskbar { get; private set; }

    /// <summary>
    /// Set when the popup lost focus to a taskbar click: resolves to true if that click was on our own
    /// taskbar button. That click relaunches the exe and signals "show" ~1s later, which must be swallowed
    /// so the click acts as a toggle instead of close-then-reopen.
    /// </summary>
    public Task<bool>? OwnButtonClickCheck { get; private set; }

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
            if (OwnButtonClickCheck != null && Application.Current is App app)
                app.ShutdownSwallowingRelaunch(OwnButtonClickCheck);
            else
                Application.Current.Shutdown();
        }
    }

    private bool _closingByTaskbarToggle;

    private void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        if (!_closingByTaskbarToggle && WindowPositioner.IsCursorOverTaskbar())
        {
            DeactivatedOnTaskbar = true;
            OwnButtonClickCheck = WindowPositioner.IsCursorOnOwnTaskbarButtonAsync();
        }
        SafeClose();
    }

    private void AppItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is AppDisplayItem item)
            LaunchItem(item);
    }

    private void LaunchItem(AppDisplayItem item)
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

    /// <summary>Close the popup (without the deactivation side effects) before opening a dialog.</summary>
    private void CloseForDialog()
    {
        Deactivated -= MainWindow_Deactivated;
        _isClosing = true;
        try { Close(); } catch { }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        CloseForDialog();
        (Application.Current as App)?.OpenSettingsDialog(_selectedGroupIndex);
    }

    private void Preferences_Click(object sender, RoutedEventArgs e)
    {
        CloseForDialog();
        (Application.Current as App)?.OpenPreferencesDialog();
    }

    // === Empty group ===

    private void UpdateEmptyState()
    {
        bool empty = _displayItems == null || _displayItems.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EmptyAdd_Click(object sender, RoutedEventArgs e)
    {
        // Open the editor on the group being viewed so apps can be added right away
        CloseForDialog();
        (Application.Current as App)?.OpenSettingsDialog(_selectedGroupIndex);
    }

    // === Quick delete (✕) with undo ===

    private sealed record PendingUndo(AppDisplayItem Item, AppEntry Entry, int Index, int GroupIndex);

    private PendingUndo? _pendingUndo;
    private System.Windows.Threading.DispatcherTimer? _undoTimer;

    private void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is AppDisplayItem item)
            DeleteItem(item);
    }

    private void DeleteItem(AppDisplayItem item)
    {
        if (_displayItems == null || _config == null) return;
        if (_selectedGroupIndex < 0 || _selectedGroupIndex >= _config.Groups.Count) return;

        try
        {
            int index = _displayItems.IndexOf(item);
            if (index < 0) return;

            // Keep the original entry (custom name etc.) so undo restores it exactly
            var group = _config.Groups[_selectedGroupIndex];
            var entry = group.Apps.FirstOrDefault(a => a.Path == item.Path)
                        ?? new AppEntry { Name = "", Path = item.Path };

            _displayItems.Remove(item);
            SaveConfigOrder();
            RelockFirstGroupHeight();
            UpdateEmptyState();

            ShowUndo(new PendingUndo(item, entry, index, _selectedGroupIndex));
        }
        catch { }
    }

    private void ShowUndo(PendingUndo undo)
    {
        _pendingUndo = undo;
        UndoText.Text = string.Format(Strings.Get("ItemRemoved"), undo.Item.Name);
        UndoBar.Visibility = Visibility.Visible;

        _undoTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _undoTimer.Tick -= UndoTimer_Tick;
        _undoTimer.Tick += UndoTimer_Tick;
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    private void UndoTimer_Tick(object? sender, EventArgs e) => HideUndo();

    private void HideUndo()
    {
        _undoTimer?.Stop();
        _pendingUndo = null;
        UndoBar.Visibility = Visibility.Collapsed;
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => UndoDelete();

    private void UndoDelete()
    {
        var undo = _pendingUndo;
        HideUndo();
        if (undo == null || _displayItems == null || _config == null) return;
        if (undo.GroupIndex != _selectedGroupIndex || undo.GroupIndex >= _config.Groups.Count) return;

        try
        {
            var group = _config.Groups[undo.GroupIndex];
            group.Apps.Insert(Math.Min(undo.Index, group.Apps.Count), undo.Entry);
            _displayItems.Insert(Math.Min(undo.Index, _displayItems.Count), undo.Item);
            SaveConfig(_config);
            RelockFirstGroupHeight();
            UpdateEmptyState();
        }
        catch { }
    }

    /// <summary>The popup height is locked to the first group's content; re-measure it after it changes.</summary>
    private void RelockFirstGroupHeight()
    {
        if (_selectedGroupIndex != 0 || !_initialHeightLocked) return;

        SizeToContent = SizeToContent.WidthAndHeight;
        UpdateLayout();
        Height = ActualHeight;
        SizeToContent = SizeToContent.Width;
    }

    // === Keyboard navigation ===
    // ↑/↓ move between apps, Enter launches, 1–9 launch directly, ←/→ or Ctrl+(Shift+)Tab switch groups,
    // Ctrl+Z undoes a quick delete, Esc closes.

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;

        var mods = Keyboard.Modifiers;
        var key = e.Key;

        if (mods == ModifierKeys.Control && key == Key.Z)
        {
            UndoDelete();
            e.Handled = true;
            return;
        }

        if (key == Key.Tab && mods.HasFlag(ModifierKeys.Control))
        {
            SwitchGroup(mods.HasFlag(ModifierKeys.Shift) ? -1 : +1);
            e.Handled = true;
            return;
        }

        if (mods != ModifierKeys.None) return;

        switch (key)
        {
            case Key.Down:
                MoveItemFocus(+1);
                e.Handled = true;
                return;
            case Key.Up:
                MoveItemFocus(-1);
                e.Handled = true;
                return;
            case Key.Left:
                SwitchGroup(-1);
                e.Handled = true;
                return;
            case Key.Right:
                SwitchGroup(+1);
                e.Handled = true;
                return;
            case Key.Enter:
                var focused = FocusedItemIndex();
                if (focused >= 0 && _displayItems != null)
                {
                    LaunchItem(_displayItems[focused]);
                    e.Handled = true;
                }
                return;
        }

        int number = key switch
        {
            >= Key.D1 and <= Key.D9 => key - Key.D1,
            >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad1,
            _ => -1
        };
        if (number >= 0 && _displayItems != null && number < _displayItems.Count)
        {
            LaunchItem(_displayItems[number]);
            e.Handled = true;
        }
    }

    private int FocusedItemIndex()
    {
        if (_displayItems == null) return -1;
        if (Keyboard.FocusedElement is FrameworkElement fe && fe.DataContext is AppDisplayItem item)
            return _displayItems.IndexOf(item);
        return -1;
    }

    private void MoveItemFocus(int delta)
    {
        if (_displayItems == null || _displayItems.Count == 0) return;

        int current = FocusedItemIndex();
        int next = current < 0
            ? (delta > 0 ? 0 : _displayItems.Count - 1)
            : (current + delta + _displayItems.Count) % _displayItems.Count;
        FocusItem(next);
    }

    private void FocusItem(int index)
    {
        if (_displayItems == null || index < 0 || index >= _displayItems.Count) return;

        AppList.ScrollIntoView(_displayItems[index]);
        var container = AppList.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
        if (container == null)
        {
            AppList.UpdateLayout();
            container = AppList.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
        }
        if (container == null) return;

        // The first Button in the item template is the launch button (the ✕ button comes after it)
        var button = FindDescendant<Button>(container);
        button?.Focus();
    }

    private void SwitchGroup(int delta)
    {
        if (_config == null || _config.Groups.Count <= 1) return;

        int count = _config.Groups.Count;
        LoadGroup((_selectedGroupIndex + delta + count) % count);
        StartBackgroundDetailsLoading();

        // Keep keyboard flow going: focus the first app of the new group once it's laid out
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => FocusItem(0));
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            var deeper = FindDescendant<T>(child);
            if (deeper != null) return deeper;
        }
        return null;
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