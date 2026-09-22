using System.Windows;
using JumpListLauncher.Helpers;
using JumpListLauncher.Models;

namespace JumpListLauncher;

public partial class PreferencesWindow : Window
{
    private readonly Preferences _prefs;

    // ComboBox items with internal value
    private record ComboItem(string Display, string Value)
    {
        public override string ToString() => Display;
    }

    public PreferencesWindow()
    {
        InitializeComponent();

        _prefs = PreferencesHelper.Load();

        // Localize labels
        Title = Strings.Get("Preferences");
        LblTheme.Text = Strings.Get("ThemeLabel");
        LblLanguage.Text = Strings.Get("LanguageLabel");
        TxtNotice.Text = Strings.Get("RestartNotice");
        BtnSave.Content = Strings.Get("Save");
        BtnCancel.Content = Strings.Get("Cancel");

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
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (CmbTheme.SelectedItem is ComboItem theme)
            _prefs.Theme = theme.Value;
        if (CmbLanguage.SelectedItem is ComboItem lang)
            _prefs.Language = lang.Value;

        PreferencesHelper.Save(_prefs);
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
