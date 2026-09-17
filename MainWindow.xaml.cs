using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using JumpListLauncher.Models;
using JumpListLauncher.Helpers;

namespace JumpListLauncher;

public class AppDisplayItem : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";

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

public partial class MainWindow : Window
{
    private readonly string _configPath;
    private List<AppDisplayItem>? _displayItems;

    public MainWindow()
    {
        InitializeComponent();
        _configPath = System.IO.Path.Combine(AppContext.BaseDirectory, "config.json");

        // Apply localized strings
        BtnSettings.Content = Strings.Get("Settings");
        BtnClose.Content = Strings.Get("Close");

        LoadConfigFast();

        Loaded += MainWindow_Loaded;
        Deactivated += MainWindow_Deactivated;
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

            // Clamp left to screen
            double workLeft = SystemParameters.WorkArea.Left;
            double workWidth = SystemParameters.WorkArea.Width;
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
                var defaultApps = new[]
                {
                    "C:\\Windows\\System32\\notepad.exe",
                    "calc.exe",
                    "C:\\Windows\\explorer.exe"
                };

                var defaultConfig = new AppConfig
                {
                    GroupName = "Groupa",
                    Apps = defaultApps.Select(p => new AppEntry
                    {
                        Name = "",  // empty = auto-detect from path based on OS language
                        Path = p
                    }).ToList()
                };
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                File.WriteAllText(_configPath, JsonSerializer.Serialize(defaultConfig, options));
            }

            var configJson = File.ReadAllText(_configPath);
            var config = JsonSerializer.Deserialize<AppConfig>(configJson);

            if (config != null)
            {
                HeaderText.Text = config.GroupName;
                _displayItems = new List<AppDisplayItem>();

                foreach (var app in config.Apps)
                {
                    // If name is empty → auto-detect from path (follows OS language)
                    // If name is set → user's custom name, use as-is
                    var displayName = string.IsNullOrWhiteSpace(app.Name)
                        ? IconExtractor.GetDisplayName(app.Path)
                        : app.Name;

                    _displayItems.Add(new AppDisplayItem
                    {
                        Name = displayName,
                        Path = app.Path,
                        Icon = null
                    });
                }

                AppList.ItemsSource = _displayItems;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{Strings.Get("ErrorLoad")}: {ex.Message}", Strings.Get("Error"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        DwmHelper.EnableMica(this);
        UpdateLayout();

        var (left, top) = WindowPositioner.CalculatePosition(this);
        Left = left;
        Top = top;

        // Store the fixed bottom anchor = top + height (this never changes)
        _bottomAnchorY = top + ActualHeight;
        _iconCenterX = left + (ActualWidth / 2.0);
        _anchored = true;

        Activate();

        // Load icons on background thread (SHGetFileInfoW is COM-based, single-thread only)
        if (_displayItems != null)
        {
            await Task.Run(() =>
            {
                foreach (var item in _displayItems)
                {
                    try
                    {
                        var bmp = IconExtractor.ExtractIcon(item.Path);
                        bmp?.Freeze();
                        Dispatcher.BeginInvoke(() => item.Icon = bmp);
                    }
                    catch { }
                }
            });
        }
    }

    private void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        Close();
    }

    private void AppItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is AppDisplayItem item)
        {
            try
            {
                Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
                Close();
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

        var editor = new ConfigEditorWindow(_configPath);
        editor.Owner = this;
        editor.ShowDialog();

        if (editor.WasSaved)
        {
            LoadConfigFast();
            if (_displayItems != null)
            {
                Task.Run(() =>
                {
                    foreach (var item in _displayItems)
                    {
                        try
                        {
                            var bmp = IconExtractor.ExtractIcon(item.Path);
                            bmp?.Freeze();
                            Dispatcher.BeginInvoke(() => item.Icon = bmp);
                        }
                        catch { }
                    }
                });
            }
        }

        Deactivated += MainWindow_Deactivated;
        Activate();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}