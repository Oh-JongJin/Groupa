using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using JumpListLauncher.Helpers;
using JumpListLauncher.Models;

namespace JumpListLauncher;

public partial class PreferencesWindow : Window
{
    private readonly Preferences _prefs;
    private string _hotkeyModifiers;
    private string _hotkeyKey;

    private record ComboItem(string Display, string Value)
    {
        public override string ToString() => Display;
    }

    public PreferencesWindow()
    {
        InitializeComponent();

        _prefs = PreferencesHelper.Load();
        _hotkeyModifiers = _prefs.HotkeyModifiers;
        _hotkeyKey = _prefs.HotkeyKey;

        // Localize labels
        Title = Strings.Get("Preferences");
        LblTheme.Text = Strings.Get("ThemeLabel");
        LblLanguage.Text = Strings.Get("LanguageLabel");
        LblHotkey.Text = Strings.Get("HotkeyLabel");
        TxtNotice.Text = Strings.Get("RestartNotice");
        BtnSave.Content = Strings.Get("Save");
        BtnCancel.Content = Strings.Get("Cancel");
        ChkHotkeyEnabled.Content = Strings.Get("HotkeyEnabled");

        // Theme ComboBox
        var themeItems = new[]
        {
            new ComboItem(Strings.Get("ThemeSystem"), "system"),
            new ComboItem(Strings.Get("ThemeDark"), "dark"),
            new ComboItem(Strings.Get("ThemeLight"), "light"),
        };
        CmbTheme.ItemsSource = themeItems;
        CmbTheme.SelectedIndex = _prefs.Theme switch
        {
            "dark" => 1,
            "light" => 2,
            _ => 0
        };

        // Language ComboBox
        var langItems = new[]
        {
            new ComboItem(Strings.Get("LangSystem"), "system"),
            new ComboItem(Strings.Get("LangKorean"), "ko"),
            new ComboItem(Strings.Get("LangEnglish"), "en"),
        };
        CmbLanguage.ItemsSource = langItems;
        CmbLanguage.SelectedIndex = _prefs.Language switch
        {
            "ko" => 1,
            "en" => 2,
            _ => 0
        };

        // Hotkey
        ChkHotkeyEnabled.IsChecked = _prefs.HotkeyEnabled;
        TxtHotkey.Text = HotkeyHelper.FormatHotkey(_hotkeyModifiers, _hotkeyKey);
        UpdateHotkeyPanelVisibility();
    }

    private void ChkHotkeyEnabled_Changed(object sender, RoutedEventArgs e)
    {
        UpdateHotkeyPanelVisibility();
    }

    private void UpdateHotkeyPanelVisibility()
    {
        HotkeyPanel.Visibility = ChkHotkeyEnabled.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TxtHotkey_GotFocus(object sender, RoutedEventArgs e)
    {
        TxtHotkey.Text = Strings.Get("HotkeyHint");
    }

    private void TxtHotkey_LostFocus(object sender, RoutedEventArgs e)
    {
        TxtHotkey.Text = HotkeyHelper.FormatHotkey(_hotkeyModifiers, _hotkeyKey);
    }

    private void TxtHotkey_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Ignore modifier-only presses
        if (key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LWin || key == Key.RWin)
            return;

        // Build modifier string
        var mods = new List<string>();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods.Add("Ctrl");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods.Add("Alt");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods.Add("Shift");

        if (mods.Count == 0) return; // Require at least one modifier

        _hotkeyModifiers = string.Join("+", mods);
        _hotkeyKey = key.ToString();
        TxtHotkey.Text = HotkeyHelper.FormatHotkey(_hotkeyModifiers, _hotkeyKey);

        // Move focus away
        BtnSave.Focus();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (CmbTheme.SelectedItem is ComboItem theme)
            _prefs.Theme = theme.Value;
        if (CmbLanguage.SelectedItem is ComboItem lang)
            _prefs.Language = lang.Value;

        _prefs.HotkeyEnabled = ChkHotkeyEnabled.IsChecked == true;
        _prefs.HotkeyModifiers = _hotkeyModifiers;
        _prefs.HotkeyKey = _hotkeyKey;

        PreferencesHelper.Save(_prefs);

        DialogResult = true;
        Close();

        // Shut down entire app so changes take effect on next launch
        Application.Current.Shutdown();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
