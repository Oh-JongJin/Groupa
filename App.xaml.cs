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
    private EventWaitHandle? _showEvent;
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
        const string mutexName = "Groupa_SingleInstance_Mutex";
        const string eventName = "Groupa_Show_Event";

        if (HotkeyMode)
        {
            bool isNew = false;
            try
            {
                _mutex = new Mutex(true, mutexName, out isNew);
            }
            catch (AbandonedMutexException)
            {
                isNew = true;
            }

            if (!isNew)
            {
                try
                {
                    using var evt = EventWaitHandle.OpenExisting(eventName);
                    evt.Set();
                }
                catch { }
                Shutdown();
                return;
            }

            try
            {
                _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            }
            catch
            {
                try { _showEvent = EventWaitHandle.OpenExisting(eventName); } catch { }
            }
        }

        // Load theme
        var themeUri = IsDarkMode
            ? new Uri("Themes/DarkTheme.xaml", UriKind.Relative)
            : new Uri("Themes/LightTheme.xaml", UriKind.Relative);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = themeUri });

        base.OnStartup(e);

        // Handle --settings / --preferences arguments
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
                ClearJumpListCache();
                IconExtractor.ClearCache();
                RegisterJumpList();
                System.Windows.MessageBox.Show(
                    Strings.IsKorean ? "캐시가 초기화되었습니다." : "Cache cleared.",
                    "Groupa", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private void StartEventListener()
    {
        Task.Run(() =>
        {
            while (_showEvent != null)
            {
                try
                {
                    if (_showEvent.WaitOne())
                    {
                        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send,
                            (Action)(() => ShowPopup(PopupTrigger.Signal)));
                    }
                }
                catch
                {
                    break;
                }
            }
        });
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
                const string mutexName = "Groupa_SingleInstance_Mutex";
                const string eventName = "Groupa_Show_Event";
                try
                {
                    _mutex = new Mutex(true, mutexName, out _);
                    _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
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
            _showEvent?.Dispose();
            _showEvent = null;
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

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(Strings.Get("EditorTitle"), null, (s, e) =>
        {
            Dispatcher.BeginInvoke(() => new ConfigEditorWindow(AppPaths.ConfigPath).ShowDialog());
        });
        menu.Items.Add(Strings.Get("Preferences"), null, (s, e) =>
        {
            Dispatcher.BeginInvoke(() => new PreferencesWindow().ShowDialog());
        });
        menu.Items.Add("-");
        menu.Items.Add(Strings.Get("Close").Replace("❌ ", ""), null, (s, e) =>
        {
            Dispatcher.BeginInvoke(() => ExitApp());
        });

        _trayIcon.ContextMenuStrip = menu;
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
                using var evt = new EventWaitHandle(false, EventResetMode.AutoReset, "Groupa_Show_Event");
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
        _showEvent?.Dispose();
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

        try
        {
            // Delete Windows Jump List cache files
            var recentDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Recent");

            var autoDest = Path.Combine(recentDir, "AutomaticDestinations");
            var customDest = Path.Combine(recentDir, "CustomDestinations");

            if (Directory.Exists(autoDest))
            {
                foreach (var f in Directory.GetFiles(autoDest, "*.automaticDestinations-ms"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            if (Directory.Exists(customDest))
            {
                foreach (var f in Directory.GetFiles(customDest, "*.customDestinations-ms"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        HotkeyHelper.Unregister();
        _trayIcon?.Dispose();
        _showEvent?.Dispose();
        if (_mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
        }
        base.OnExit(e);
    }
}
