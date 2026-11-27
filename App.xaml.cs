using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Shell;
using JumpListLauncher.Helpers;

namespace JumpListLauncher;

public partial class App : Application
{
    public static bool IsDarkMode { get; private set; }
    public static bool HotkeyMode { get; private set; }

    private Mutex? _mutex;
    // Indexed like InstanceSignals.All: [0]=Show, [1]=ClearCache, [2]=Settings, [3]=Preferences
    private EventWaitHandle[]? _signalEvents;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private MainWindow? _activePopup;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Load preferences
        var prefs = PreferencesHelper.Load();
        Strings.SetLanguage(prefs.Language);
        IsDarkMode = PreferencesHelper.ResolveDarkMode(prefs);
        HotkeyMode = prefs.HotkeyEnabled;

        // Single instance check (only when hotkey mode is on)
        if (HotkeyMode)
        {
            bool isNew = false;
            try
            {
                _mutex = new Mutex(true, MutexName, out isNew);
            }
            catch (AbandonedMutexException)
            {
                isNew = true;
            }

            if (!isNew)
            {
                // Another instance is resident: forward this command line to it (Program.Main normally
                // already did this; this covers races). Unknown arguments fall back to showing the popup.
                if (!InstanceSignals.TrySignal(e.Args))
                    InstanceSignals.TrySignal(Array.Empty<string>());
                Shutdown();
                return;
            }

            _signalEvents = CreateSignalEvents();
        }

        LoadThemeResources();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        base.OnStartup(e);

        // Handle --settings / --preferences / --clear-cache arguments (no resident instance)
        if (e.Args.Length > 0)
        {
            if (e.Args[0] == "--settings")
            {
                new ConfigEditorWindow(AppPaths.ConfigPath).ShowDialog();
                Shutdown();
                return;
            }
            else if (e.Args[0] == "--preferences")
            {
                new PreferencesWindow().ShowDialog();
                Shutdown();
                return;
            }
            else if (e.Args[0] == "--clear-cache")
            {
                ClearCachesAndNotify();
                Shutdown();
                return;
            }
        }

        // Pre-search taskbar button
        Task.Run(WindowPositioner.RefreshTaskbarButtonCache);

        // Register Jump List asynchronously in background so popup opens instantly
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, (Action)(() =>
        {
            RegisterJumpList();
        }));

        if (HotkeyMode)
        {
            // Setup tray icon and hotkey
            SetupTrayIcon(prefs);
            SetupHotkey(prefs);
            StartEventListener();
        }

        // Show popup on first launch
        ShowPopup(PopupTrigger.Startup);
    }

    public enum PopupTrigger { Startup, Hotkey, Signal, Tray }

    private bool _showPending;
    // Set when the popup closed because the user clicked the taskbar; see MainWindow.OwnButtonClickCheck
    private Task<bool>? _ownButtonCloseCheck;
    private DateTime _ownButtonCloseUtc;
    private DateTime _lastTaskbarCloseUtc;

    public async void ShowPopup(PopupTrigger trigger)
    {
        // If popup is already open, toggle it closed
        if (_activePopup != null && _activePopup.IsLoaded)
        {
            _activePopup.SafeClose();
            return;
        }

        // Clicking our taskbar button while the popup is open first closes it (focus loss), then the
        // relaunched exe signals "show" ~1s later. Swallow that one signal so the click acts as a toggle.
        if (trigger == PopupTrigger.Signal && _ownButtonCloseCheck is { } check)
        {
            _ownButtonCloseCheck = null;
            if ((DateTime.UtcNow - _ownButtonCloseUtc).TotalSeconds < 3)
            {
                bool own = false;
                try { own = await check; } catch { }
                if (own) return;
            }
        }

        // Same for the tray icon: mouse-down closes the popup, then MouseClick fires on mouse-up.
        if (trigger == PopupTrigger.Tray && (DateTime.UtcNow - _lastTaskbarCloseUtc).TotalMilliseconds < 500)
            return;

        // Ignore repeated triggers while the taskbar button lookup is in flight
        if (_showPending) return;
        _showPending = true;
        try
        {
            await WindowPositioner.UpdateCursorPositionAsync();
        }
        catch { }
        finally
        {
            _showPending = false;
        }

        var mainWindow = TakePrewarmedOrNewWindow();
        _activePopup = mainWindow;
        mainWindow.Closed += (s, e) =>
        {
            _activePopup = null;
            SchedulePrewarm();
            if (mainWindow.DeactivatedOnTaskbar)
                _lastTaskbarCloseUtc = DateTime.UtcNow;
            if (mainWindow.OwnButtonClickCheck is { } ownCheck)
            {
                _ownButtonCloseCheck = ownCheck;
                _ownButtonCloseUtc = DateTime.UtcNow;
            }
        };
        mainWindow.ShowPopupWindow();
    }

    // === Pre-warmed popup (background mode only) ===
    // Building MainWindow (XAML parse, config load, icon cache lookups) is the bulk of the show latency,
    // so after a popup closes we build the next one while idle and just Show() it on the next trigger.
    private MainWindow? _prewarmed;
    private (DateTime Config, DateTime Prefs) _prewarmedStamp;

    private static (DateTime Config, DateTime Prefs) GetSettingsStamp()
    {
        try
        {
            return (File.GetLastWriteTimeUtc(AppPaths.ConfigPath), File.GetLastWriteTimeUtc(AppPaths.PreferencesPath));
        }
        catch
        {
            return (DateTime.MinValue, DateTime.MinValue);
        }
    }

    private void SchedulePrewarm()
    {
        if (!HotkeyMode || _prewarmed != null) return;

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, (Action)(() =>
        {
            if (!HotkeyMode || _prewarmed != null || _activePopup != null) return;
            try
            {
                // Take the stamp first: if the ctor itself writes a default config, the window is discarded later
                var stamp = GetSettingsStamp();
                var window = new MainWindow(prewarm: true);
                if (!window.LoadFailed)
                    window.PrepareHandle();
                _prewarmedStamp = stamp;
                _prewarmed = window;
            }
            catch
            {
                _prewarmed = null;
            }
        }));
    }

    private MainWindow TakePrewarmedOrNewWindow()
    {
        var window = _prewarmed;
        _prewarmed = null;

        // Discard if config/preferences changed since it was built (editor, reorder, language/theme…),
        // or if loading failed (a fresh window shows the error to the user).
        if (window != null && !window.LoadFailed && GetSettingsStamp() == _prewarmedStamp)
            return window;

        DiscardWindow(window);
        return new MainWindow();
    }

    // A constructed Window is registered in Application.Windows; closing it (even never shown) unregisters it.
    private static void DiscardWindow(MainWindow? window)
    {
        if (window == null) return;
        try { window.Close(); } catch { }
    }

    private static bool _highContrastApplied;

    private void LoadThemeResources()
    {
        var themeUri = IsDarkMode
            ? new Uri("Themes/DarkTheme.xaml", UriKind.Relative)
            : new Uri("Themes/LightTheme.xaml", UriKind.Relative);
        var theme = new ResourceDictionary { Source = themeUri };

        // Windows high contrast: replace every theme color with the user's system colors. Done on the
        // dictionary before it is used, so the styles inside it (StaticResource) pick up these values.
        _highContrastApplied = SystemParameters.HighContrast;
        if (_highContrastApplied)
            ApplyHighContrastColors(theme);

        Resources.MergedDictionaries.Clear();
        Resources.MergedDictionaries.Add(theme);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("Themes/Controls.xaml", UriKind.Relative) });
    }

    private static void ApplyHighContrastColors(ResourceDictionary d)
    {
        static System.Windows.Media.Color Alpha(System.Windows.Media.Color c, byte a) =>
            System.Windows.Media.Color.FromArgb(a, c.R, c.G, c.B);
        void Brush(string key, System.Windows.Media.Color c)
        {
            var b = new System.Windows.Media.SolidColorBrush(c);
            b.Freeze();
            d[key] = b;
        }

        var window = System.Windows.SystemColors.WindowColor;
        var text = System.Windows.SystemColors.WindowTextColor;
        var highlight = System.Windows.SystemColors.HighlightColor;
        var highlightText = System.Windows.SystemColors.HighlightTextColor;
        var gray = System.Windows.SystemColors.GrayTextColor;
        var face = System.Windows.SystemColors.ControlColor;
        var hover = Alpha(highlight, 0x55);
        var pressed = Alpha(highlight, 0x80);

        // Popup
        Brush("PopupBackground", window);
        Brush("PopupBorder", text);
        Brush("HeaderForeground", text);
        Brush("ItemForeground", text);
        Brush("ItemHoverBackground", hover);
        Brush("ItemFocusBackground", pressed);
        Brush("SeparatorBrush", gray);
        Brush("FooterItemForeground", text);
        Brush("TabSelectedBackground", hover);
        Brush("TabSelectedBorder", highlight);
        Brush("TabSelectedForeground", text);
        d["HoverColor"] = hover;
        d["TransparentColor"] = Alpha(window, 0);

        // Settings windows
        Brush("EditorWindowBg", window);
        Brush("EditorButtonBg", face);
        Brush("EditorButtonHover", hover);
        Brush("EditorButtonPressed", pressed);
        Brush("EditorInputBg", window);
        Brush("EditorBorderColor", text);
        Brush("EditorSelectedBg", pressed);
        Brush("EditorSecondaryText", gray);
        Brush("EditorAccent", highlight);
        Brush("EditorAccentForeground", highlightText);
    }

    /// <summary>
    /// Apply saved preferences to the running process: theme, language and hotkey/background mode.
    /// New windows pick these up; the pre-warmed popup is rebuilt, tray menu and jump list are relabeled.
    /// </summary>
    public void ApplyPreferences()
    {
        var prefs = PreferencesHelper.Load();

        Strings.SetLanguage(prefs.Language);
        var dark = PreferencesHelper.ResolveDarkMode(prefs);
        if (dark != IsDarkMode)
        {
            IsDarkMode = dark;
            LoadThemeResources();
        }

        // The pre-warmed popup was built with the old theme/strings
        DiscardWindow(_prewarmed);
        _prewarmed = null;

        if (_trayIcon != null)
            _trayIcon.ContextMenuStrip = BuildTrayMenu();
        RegisterJumpList();

        ReloadHotkey();
        SchedulePrewarm();
    }

    /// <summary>
    /// Windows light/dark or high contrast switch while running (matters in background mode, where the
    /// process stays up). Light/dark only counts when the theme preference is "system".
    /// </summary>
    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        // The light/dark switch arrives as WM_SETTINGCHANGE "ImmersiveColorSet" → General
        if (e.Category is not (Microsoft.Win32.UserPreferenceCategory.General
            or Microsoft.Win32.UserPreferenceCategory.Color
            or Microsoft.Win32.UserPreferenceCategory.VisualStyle
            or Microsoft.Win32.UserPreferenceCategory.Accessibility)) return;
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var prefs = PreferencesHelper.Load();
                var dark = PreferencesHelper.ResolveDarkMode(prefs); // fixed dark/light ignores Windows
                bool highContrast = SystemParameters.HighContrast;
                // Color category: high contrast colors themselves may have changed
                bool colorsChanged = highContrast && e.Category == Microsoft.Win32.UserPreferenceCategory.Color;
                if (dark == IsDarkMode && highContrast == _highContrastApplied && !colorsChanged) return;

                IsDarkMode = dark;
                LoadThemeResources();
                DiscardWindow(_prewarmed);
                _prewarmed = null;
                SchedulePrewarm();
            }
            catch { }
        });
    }

    private void SetupHotkey(Models.Preferences prefs)
    {
        bool ok = HotkeyHelper.Register(prefs.HotkeyModifiers, prefs.HotkeyKey, () => Dispatcher.BeginInvoke(() => ShowPopup(PopupTrigger.Hotkey)));
        if (!ok && _trayIcon != null)
        {
            var hotkeyText = HotkeyHelper.FormatHotkey(prefs.HotkeyModifiers, prefs.HotkeyKey);
            _trayIcon.ShowBalloonTip(3000, "Groupa",
                Strings.IsKorean
                    ? $"단축키({hotkeyText}) 등록에 실패했습니다. 다른 프로그램과 중복될 수 있습니다."
                    : $"Failed to register hotkey ({hotkeyText}). It may be in conflict with another app.",
                System.Windows.Forms.ToolTipIcon.Warning);
        }
    }

    private const string MutexName = "Groupa_SingleInstance_Mutex";

    private static EventWaitHandle[]? CreateSignalEvents()
    {
        var events = new List<EventWaitHandle>();
        foreach (var name in InstanceSignals.All)
        {
            try
            {
                events.Add(new EventWaitHandle(false, EventResetMode.AutoReset, name));
            }
            catch
            {
                try { events.Add(EventWaitHandle.OpenExisting(name)); }
                catch { foreach (var ev in events) ev.Dispose(); return null; }
            }
        }
        return events.ToArray();
    }

    private void DisposeSignalEvents()
    {
        if (_signalEvents == null) return;
        foreach (var ev in _signalEvents) ev.Dispose();
        _signalEvents = null;
    }

    /// <summary>
    /// Listens for requests forwarded by relaunched instances (taskbar click, jump-list commands).
    /// </summary>
    private void StartEventListener()
    {
        var events = _signalEvents;
        if (events == null) return;

        Task.Run(() =>
        {
            while (true)
            {
                int index;
                try { index = WaitHandle.WaitAny(events); }
                catch { break; } // disposed: background mode turned off / exiting

                Action? action = index switch
                {
                    0 => () => ShowPopup(PopupTrigger.Signal),
                    1 => () => { _activePopup?.SafeClose(); ClearCachesAndNotify(); },
                    2 => () => { _activePopup?.SafeClose(); OpenSettingsDialog(); },
                    3 => () => { _activePopup?.SafeClose(); OpenPreferencesDialog(); },
                    _ => null
                };
                if (action != null)
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send, action);
            }
        });
    }

    // === Dialogs (shared by popup buttons, tray menu and forwarded jump-list commands) ===

    /// <summary>Brings an already open settings/preferences dialog to the front instead of stacking another.</summary>
    private bool ActivateOpenDialog()
    {
        foreach (Window w in Windows)
        {
            if ((w is ConfigEditorWindow || w is PreferencesWindow) && w.IsVisible)
            {
                if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                w.Activate();
                return true;
            }
        }
        return false;
    }

    public void OpenSettingsDialog(int groupIndex = 0)
    {
        if (ActivateOpenDialog()) return;

        new ConfigEditorWindow(AppPaths.ConfigPath, groupIndex).ShowDialog();

        if (!HotkeyMode)
            Shutdown();
    }

    public void OpenPreferencesDialog()
    {
        if (ActivateOpenDialog()) return;

        new PreferencesWindow().ShowDialog(); // applies on save via ApplyPreferences

        if (!HotkeyMode)
            Shutdown();
    }

    /// <summary>Jump list "Clear Cache": wipe icon/name caches (incl. the pre-warmed popup) and re-register.</summary>
    private void ClearCachesAndNotify()
    {
        ClearJumpListCache();
        IconExtractor.ClearCache();
        DiscardWindow(_prewarmed);
        _prewarmed = null;
        RegisterJumpList();

        ThemedDialog.Show(
            Strings.IsKorean ? "캐시가 초기화되었습니다." : "Cache cleared.",
            "Groupa", MessageBoxButton.OK, MessageBoxImage.Information);

        SchedulePrewarm();
    }

    /// <summary>
    /// Dynamically apply hotkey preference changes without restart.
    /// </summary>
    public void ReloadHotkey()
    {
        var prefs = PreferencesHelper.Load();
        HotkeyMode = prefs.HotkeyEnabled;

        // Unregister existing hotkey
        HotkeyHelper.Unregister();

        if (HotkeyMode)
        {
            if (_trayIcon == null)
            {
                SetupTrayIcon(prefs);
            }
            else
            {
                var hotkeyText = HotkeyHelper.FormatHotkey(prefs.HotkeyModifiers, prefs.HotkeyKey);
                var tip = $"Groupa ({hotkeyText})";
                if (tip.Length > 63) tip = tip.Substring(0, 63);
                _trayIcon.Text = tip;
            }

            SetupHotkey(prefs);

            // Setup single instance mutex if not exists
            if (_mutex == null)
            {
                try
                {
                    _mutex = new Mutex(true, MutexName, out _);
                    _signalEvents = CreateSignalEvents();
                    StartEventListener();
                }
                catch { }
            }
        }
        else
        {
            DiscardWindow(_prewarmed);
            _prewarmed = null;

            // Disable: remove tray icon
            _trayIcon?.Dispose();
            _trayIcon = null;
            DisposeSignalEvents();
            if (_mutex != null)
            {
                try { _mutex.ReleaseMutex(); } catch { }
                _mutex.Dispose();
                _mutex = null;
            }

            // If no window is visible, shut down cleanly
            if (_activePopup == null || !_activePopup.IsLoaded)
            {
                Shutdown();
            }
        }
    }

    private void SetupTrayIcon(Models.Preferences prefs)
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon();

        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath != null)
                _trayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
        }
        catch
        {
            _trayIcon.Icon = SystemIcons.Application;
        }

        var hotkeyText = HotkeyHelper.FormatHotkey(prefs.HotkeyModifiers, prefs.HotkeyKey);
        var tip = $"Groupa ({hotkeyText})";
        if (tip.Length > 63) tip = tip.Substring(0, 63);
        _trayIcon.Text = tip;
        _trayIcon.Visible = true;

        _trayIcon.MouseClick += (s, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
                Dispatcher.BeginInvoke(() => ShowPopup(PopupTrigger.Tray));
        };

        _trayIcon.ContextMenuStrip = BuildTrayMenu();
    }

    private System.Windows.Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(Strings.Get("EditorTitle"), null, (s, e) =>
        {
            Dispatcher.BeginInvoke(() => { _activePopup?.SafeClose(); OpenSettingsDialog(); });
        });
        menu.Items.Add(Strings.Get("Preferences"), null, (s, e) =>
        {
            Dispatcher.BeginInvoke(() => { _activePopup?.SafeClose(); OpenPreferencesDialog(); });
        });
        menu.Items.Add("-");
        menu.Items.Add(Strings.Get("Close"), null, (s, e) =>
        {
            Dispatcher.BeginInvoke(() => ExitApp());
        });

        return menu;
    }

    /// <summary>
    /// Non-background mode: the popup closed because our own taskbar button was clicked. That click
    /// relaunches the exe ~1s later, which would open a fresh popup. Briefly own the show event so the
    /// relaunched instance just signals it and exits (Program.Main), then shut down.
    /// </summary>
    public async void ShutdownSwallowingRelaunch(Task<bool> ownButtonCheck)
    {
        bool own = false;
        try { own = await ownButtonCheck; } catch { }

        if (own)
        {
            try
            {
                using var evt = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceSignals.Show);
                await Task.Run(() => evt.WaitOne(3000));
            }
            catch { }
        }

        Shutdown();
    }

    private void ExitApp()
    {
        HotkeyHelper.Unregister();
        _trayIcon?.Dispose();
        DisposeSignalEvents();
        if (_mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
        }
        Shutdown();
    }

    private void RegisterJumpList()
    {
        var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
        var jumpList = new JumpList();
        jumpList.ShowRecentCategory = false;
        jumpList.ShowFrequentCategory = false;

        jumpList.JumpItems.Add(new JumpTask
        {
            Title = Strings.IsKorean ? "캐시 초기화" : "Clear Cache",
            Description = Strings.IsKorean ? "우클릭 메뉴 캐시 초기화" : "Clear right-click menu cache",
            ApplicationPath = exePath,
            Arguments = "--clear-cache",
            IconResourcePath = Path.Combine(Environment.SystemDirectory, "shell32.dll"),
            IconResourceIndex = 238,
            CustomCategory = ""
        });

        JumpList.SetJumpList(this, jumpList);
        try
        {
            jumpList.Apply();
        }
        catch { }
    }

    private void ClearJumpListCache()
    {
        try
        {
            // Clear WPF JumpList
            var jumpList = new JumpList();
            jumpList.ShowRecentCategory = false;
            jumpList.ShowFrequentCategory = false;
            JumpList.SetJumpList(this, jumpList);
            jumpList.Apply();
        }
        catch { }

        // NOTE: Never delete files under %APPDATA%\Microsoft\Windows\Recent\{Automatic,Custom}Destinations.
        // Those folders hold EVERY app's jump list, including File Explorer's Quick Access pins
        // (f01b4d95cf55d32a.automaticDestinations-ms). Re-applying our own JumpList above is enough
        // to reset Groupa's entries.
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        HotkeyHelper.Unregister();
        _trayIcon?.Dispose();
        DisposeSignalEvents();
        if (_mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
        }
        base.OnExit(e);
    }
}
