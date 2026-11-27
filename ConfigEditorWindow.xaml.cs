using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
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
        _configPath = configPath;
        _initialGroupIndex = initialGroupIndex;

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
        BtnRename.Content = Strings.Get("Rename");
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
                CmbGroup.SelectedIndex = Math.Clamp(_initialGroupIndex, 0, _config.Groups.Count - 1);
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

    private void AddItem(AppEditItem item)
    {
        _items.Add(item);
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
            item.Name = textBox.Text.Trim();
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

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == System.Windows.Input.Key.Escape)
            Close();
    }
}
