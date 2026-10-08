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
    private Window? _hiddenWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Load preferences
        var prefs = PreferencesHelper.Load();
        Strings.SetLanguage(prefs.Language);
        IsDarkMode = PreferencesHelper.ResolveDarkMode(prefs);
        HotkeyMode = prefs.HotkeyEnabled;

        // Load theme
        var themeUri = IsDarkMode
            ? new Uri("Themes/DarkTheme.xaml", UriKind.Relative)
            : new Uri("Themes/LightTheme.xaml", UriKind.Relative);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = themeUri });

        base.OnStartup(e);

        // Handle --settings / --preferences arguments (before mutex check)
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
        }
        // Handle --clear-cache before anything else
        if (e.Args.Length > 0 && e.Args[0] == "--clear-cache")
        {
            ClearJumpListCache();
            System.Windows.MessageBox.Show(
                Strings.IsKorean ? "캐시가 초기화되었습니다." : "Cache cleared.",
                "Groupa", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Single instance check (only when hotkey mode is on)
        const string mutexName = "Groupa_SingleInstance_Mutex";
        const string eventName = "Groupa_Show_Event";

        if (HotkeyMode)
        {
            _mutex = new Mutex(true, mutexName, out bool isNew);
            if (!isNew)
            {
                try
                {
                    var evt = EventWaitHandle.OpenExisting(eventName);
                    evt.Set();
                }
                catch { }
                Shutdown();
                return;
            }
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        }

        // Pre-search taskbar button
        Task.Run(() => WindowPositioner.PreFindTaskbarButton());

        // Register Jump List with cache clear option
        RegisterJumpList();

        if (HotkeyMode)
        {
            // === Background mode: tray icon + hotkey ===
            _hiddenWindow = new Window
            {
                Width = 0, Height = 0,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Visibility = Visibility.Hidden
            };
            _hiddenWindow.Show();
            _hiddenWindow.Hide();

            HotkeyHelper.Register(_hiddenWindow, prefs.HotkeyModifiers, prefs.HotkeyKey, ShowPopup);
            SetupTrayIcon(prefs);

            // Listen for signal from other instances
            Task.Run(() =>
            {
                while (_showEvent != null && _showEvent.WaitOne())
                {
                    Dispatcher.BeginInvoke(ShowPopup);
                }
            });
        }

        // Show popup on first launch
        ShowPopup();
    }

    public void ShowPopup()
    {
        // If popup is already open, just activate it
        if (_activePopup != null && _activePopup.IsLoaded)
        {
            _activePopup.Activate();
            return;
        }

        Task.Run(() => WindowPositioner.PreFindTaskbarButton());
        var mainWindow = new MainWindow();
        _activePopup = mainWindow;
        mainWindow.Closed += (s, e) => _activePopup = null;
        mainWindow.Show();
    }

    private MainWindow? _activePopup;

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
            // Ensure hidden window exists
            if (_hiddenWindow == null)
            {
                _hiddenWindow = new Window
                {
                    Width = 0, Height = 0,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Visibility = Visibility.Hidden
                };
                _hiddenWindow.Show();
                _hiddenWindow.Hide();
            }

            HotkeyHelper.Register(_hiddenWindow, prefs.HotkeyModifiers, prefs.HotkeyKey, ShowPopup);

            // Setup tray if not exists
            if (_trayIcon == null)
                SetupTrayIcon(prefs);
            else
                _trayIcon.Text = $"Groupa ({HotkeyHelper.FormatHotkey(prefs.HotkeyModifiers, prefs.HotkeyKey)})";

            // Setup single instance mutex if not exists
            if (_mutex == null)
            {
                const string mutexName = "Groupa_SingleInstance_Mutex";
                const string eventName = "Groupa_Show_Event";
                _mutex = new Mutex(true, mutexName, out _);
                _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
                Task.Run(() =>
                {
                    while (_showEvent != null && _showEvent.WaitOne())
                    {
                        Dispatcher.BeginInvoke(ShowPopup);
                    }
                });
            }
        }
        else
        {
            // Disable: remove tray icon
            _trayIcon?.Dispose();
            _trayIcon = null;
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
        _trayIcon.Text = $"Groupa ({hotkeyText})";
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
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
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
            CustomCategory = ""
        });

        JumpList.SetJumpList(this, jumpList);
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
        _trayIcon?.Dispose();
        _showEvent?.Dispose();
        base.OnExit(e);
    }
}
