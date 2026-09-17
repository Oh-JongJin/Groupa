using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using JumpListLauncher.Helpers;
using JumpListLauncher.Models;
using Microsoft.Win32;

namespace JumpListLauncher;

public class AppEditItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string DisplayText => string.IsNullOrWhiteSpace(Name)
        ? $"[auto] {IconExtractor.GetDisplayName(Path)}  →  {Path}"
        : $"{Name}  →  {Path}";
}

public partial class ConfigEditorWindow : Window
{
    private readonly string _configPath;
    private readonly ObservableCollection<AppEditItem> _items = new();

    public bool WasSaved { get; private set; }

    public ConfigEditorWindow(string configPath)
    {
        InitializeComponent();
        _configPath = configPath;

        // Apply theme background
        Background = (System.Windows.Media.Brush)FindResource("EditorWindowBg");

        // Apply localized strings
        Title = Strings.Get("EditorTitle");
        LblGroupName.Text = Strings.Get("GroupName");
        LblAppList.Text = Strings.Get("AppList");
        BtnAdd.Content = Strings.Get("AddApp");
        BtnRemove.Content = Strings.Get("Remove");
        BtnUp.Content = Strings.Get("MoveUp");
        BtnDown.Content = Strings.Get("MoveDown");
        BtnSave.Content = Strings.Get("Save");
        BtnCancel.Content = Strings.Get("Cancel");

        AppListBox.ItemsSource = _items;

        LoadConfig();
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                var config = JsonSerializer.Deserialize<AppConfig>(json);
                if (config != null)
                {
                    GroupNameBox.Text = config.GroupName;
                    _items.Clear();
                    foreach (var app in config.Apps)
                    {
                        _items.Add(new AppEditItem { Name = app.Name, Path = app.Path });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{Strings.Get("ErrorLoad")}: {ex.Message}", Strings.Get("Error"),
                MessageBoxButton.OK, MessageBoxImage.Error);
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
            // Name is empty → auto-detect at runtime
            _items.Add(new AppEditItem
            {
                Name = "",
                Path = dialog.FileName
            });
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
            var config = new AppConfig
            {
                GroupName = GroupNameBox.Text.Trim(),
                Apps = new()
            };

            foreach (var item in _items)
            {
                config.Apps.Add(new AppEntry { Name = item.Name, Path = item.Path });
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            var json = JsonSerializer.Serialize(config, options);
            File.WriteAllText(_configPath, json);

            WasSaved = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{Strings.Get("ErrorLoad")}: {ex.Message}", Strings.Get("Error"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
