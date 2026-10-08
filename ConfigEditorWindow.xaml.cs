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
    private AppConfig _config = new();
    private int _selectedGroupIndex;

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
        BtnAddGroup.Content = Strings.Get("AddGroup");
        BtnDeleteGroup.Content = Strings.Get("DeleteGroup");
        BtnAddFolder.Content = Strings.Get("AddFolder");
        BtnAddUrl.Content = Strings.Get("AddUrl");
        LblGroup.Text = Strings.Get("GroupLabel");

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
                CmbGroup.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{Strings.Get("ErrorLoad")}: {ex.Message}", Strings.Get("Error"),
                MessageBoxButton.OK, MessageBoxImage.Error);
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

    private void LoadGroupApps(int index)
    {
        if (index < 0 || index >= _config.Groups.Count) return;
        var group = _config.Groups[index];
        GroupNameBox.Text = group.GroupName;
        _items.Clear();
        foreach (var app in group.Apps)
        {
            _items.Add(new AppEditItem { Name = app.Name, Path = app.Path });
        }
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
        var result = MessageBox.Show(msg, Strings.Get("Confirm"),
            MessageBoxButton.YesNo, MessageBoxImage.Question);

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
            _items.Add(new AppEditItem
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
            _items.Add(new AppEditItem
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

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        var label = new System.Windows.Controls.TextBlock
        {
            Text = Strings.Get("UrlInputPrompt"),
            Foreground = (System.Windows.Media.Brush)FindResource("HeaderForeground"),
            FontSize = 13, Margin = new Thickness(0, 0, 0, 8)
        };
        var textBox = new System.Windows.Controls.TextBox
        {
            FontSize = 13, Padding = new Thickness(6, 4, 6, 4),
            Text = "https://"
        };
        var btnOk = new System.Windows.Controls.Button
        {
            Content = "OK", Width = 80, Height = 30,
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        btnOk.Click += (s, ev) => { inputWindow.DialogResult = true; inputWindow.Close(); };

        panel.Children.Add(label);
        panel.Children.Add(textBox);
        panel.Children.Add(btnOk);
        inputWindow.Content = panel;

        if (inputWindow.ShowDialog() == true && !string.IsNullOrWhiteSpace(textBox.Text))
        {
            var url = textBox.Text.Trim();
            _items.Add(new AppEditItem
            {
                Name = url,
                Path = url
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
            MessageBox.Show($"{Strings.Get("ErrorLoad")}: {ex.Message}", Strings.Get("Error"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
