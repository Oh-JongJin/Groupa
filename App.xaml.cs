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
        Task.Run(() => WindowPositioner.PreFindTaskbarButton());

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
        ShowPopup();
    }

    public void ShowPopup()
    {
        // If popup is already open, toggle it closed
        if (_activePopup != null && _activePopup.IsLoaded)
        {
            _activePopup.SafeClose();
            return;
        }

        WindowPositioner.UpdateCursorPosition();
        var mainWindow = new MainWindow();
        _activePopup = mainWindow;
        mainWindow.Closed += (s, e) => _activePopup = null;
        mainWindow.Show();
        mainWindow.Activate();
    }

    private void SetupHotkey(Models.Preferences prefs)
    {
        bool ok = HotkeyHelper.Register(prefs.HotkeyModifiers, prefs.HotkeyKey, () => Dispatcher.BeginInvoke(ShowPopup));
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
                        Dispatcher.BeginInvoke(ShowPopup);
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
                Dispatcher.BeginInvoke(ShowPopup);
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
