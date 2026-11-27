using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace JumpListLauncher.Helpers;

public static class IconExtractor
{
    private static readonly ConcurrentDictionary<string, BitmapSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> _displayNameCache = new(StringComparer.OrdinalIgnoreCase);

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_SMALLICON = 0x1;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint SHGFI_DISPLAYNAME = 0x200;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc,
        [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    private const uint SIGDN_NORMALDISPLAY = 0;

    /// <summary>
    /// Inbox Windows executables whose file description isn't localized (e.g. calc.exe reports
    /// "Windows Calculator" on a Korean system) or differs from the Start menu name. Their names are
    /// read from the Start menu (shell:AppsFolder), so Windows supplies the localized text.
    /// </summary>
    private static readonly Dictionary<string, string> InboxAppIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["calc.exe"] = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
        ["explorer.exe"] = "Microsoft.Windows.Explorer",
        ["notepad.exe"] = "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App",
        ["mspaint.exe"] = "Microsoft.Paint_8wekyb3d8bbwe!App",
        ["snippingtool.exe"] = "Microsoft.ScreenSketch_8wekyb3d8bbwe!App",
    };

    private static string? GetInboxAppName(string path, string resolvedPath)
    {
        var fileName = Path.GetFileName(path);
        if (!InboxAppIds.TryGetValue(fileName, out var appId)) return null;

        // Only for the Windows copy (or a bare name resolved via PATH), not a same-named file elsewhere
        bool isBareName = !Path.IsPathRooted(path);
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!isBareName && !resolvedPath.StartsWith(winDir + "\\", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var iid = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName("shell:AppsFolder\\" + appId, IntPtr.Zero, ref iid, out var item);
            item.GetDisplayName(SIGDN_NORMALDISPLAY, out var name);
            Marshal.ReleaseComObject(item);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            return null; // app not installed (e.g. classic Paint only) → fall back to file info
        }
    }

    /// <summary>"Tool.exe" → "Tool" for files; Explorer shows extensions when "File name extensions" is on.</summary>
    private static string StripExtension(string name, string resolvedPath)
    {
        if (Directory.Exists(resolvedPath)) return name;
        var ext = Path.GetExtension(resolvedPath);
        if (!string.IsNullOrEmpty(ext) && name.EndsWith(ext, StringComparison.OrdinalIgnoreCase) && name.Length > ext.Length)
            return name.Substring(0, name.Length - ext.Length);
        return name;
    }

    public static BitmapSource? GetCachedIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return _iconCache.TryGetValue(path, out var bmp) ? bmp : null;
    }

    public static string? GetCachedDisplayName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return _displayNameCache.TryGetValue(path, out var name) ? name : null;
    }

    public static void ClearCache()
    {
        _iconCache.Clear();
        _displayNameCache.Clear();
    }

    public static BitmapSource? ExtractIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (_iconCache.TryGetValue(path, out var cached))
            return cached;

        // For URLs — use the default browser's icon
        if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var browserPath = GetDefaultBrowserPath();
                if (!string.IsNullOrEmpty(browserPath) && File.Exists(browserPath))
                {
                    var browserIcon = ExtractIcon(browserPath);
                    if (browserIcon != null)
                    {
                        _iconCache[path] = browserIcon;
                        return browserIcon;
                    }
                }
            }
            catch { }

            // Fallback: generic .url internet shortcut icon
            try
            {
                var shinfo = new SHFILEINFO();
                SHGetFileInfoW(".url", 0x80, ref shinfo, (uint)Marshal.SizeOf(shinfo),
                    SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);
                if (shinfo.hIcon != IntPtr.Zero)
                {
                    var icon = System.Drawing.Icon.FromHandle(shinfo.hIcon);
                    var bmp = Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle,
                        new Int32Rect(0, 0, icon.Width, icon.Height),
                        BitmapSizeOptions.FromEmptyOptions());
                    DestroyIcon(shinfo.hIcon);
                    bmp.Freeze();
                    _iconCache[path] = bmp;
                    return bmp;
                }
            }
            catch { }
            return null;
        }

        try
        {
            // Try resolving short names like "calc.exe" via system PATH
            string resolvedPath = path;
            if (!File.Exists(path) && !Directory.Exists(path) && !Path.IsPathRooted(path))
            {
                var found = FindInPath(path);
                if (found != null)
                    resolvedPath = found;
                // If not found in PATH, still try SHGetFileInfoW — it may handle it
            }

            var shinfo = new SHFILEINFO();
            uint flags = SHGFI_ICON | SHGFI_SMALLICON;

            // If file doesn't physically exist, use SHGFI_USEFILEATTRIBUTES
            // to get icon based on file extension
            if (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
            {
                flags |= SHGFI_USEFILEATTRIBUTES;
            }

            var res = SHGetFileInfoW(resolvedPath, 0, ref shinfo,
                (uint)Marshal.SizeOf(shinfo), flags);

            if (res == IntPtr.Zero || shinfo.hIcon == IntPtr.Zero)
                return null;

            using var icon = System.Drawing.Icon.FromHandle(shinfo.hIcon);
            var bitmapSource = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            DestroyIcon(shinfo.hIcon);
            bitmapSource.Freeze();
            _iconCache[path] = bitmapSource;
            return bitmapSource;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolve a filename like "calc.exe" by searching the system PATH.
    /// </summary>
    private static string? FindInPath(string fileName)
    {
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? Array.Empty<string>();
        foreach (var dir in pathDirs)
        {
            try
            {
                var fullPath = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(fullPath))
                    return fullPath;
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Gets the localized display name of a file using Windows shell.
    /// e.g. "notepad.exe" → "Notepad" (en) / "메모장" (ko)
    /// </summary>
    public static string GetDisplayName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Path.GetFileNameWithoutExtension(path) ?? path;

        if (_displayNameCache.TryGetValue(path, out var cachedName))
            return cachedName;

        // Try resolving short paths
        string resolvedPath = path;
        if (!File.Exists(path) && !Path.IsPathRooted(path))
        {
            var found = FindInPath(path);
            if (found != null) resolvedPath = found;
        }

        // 0. Inbox Windows apps: use the Start menu's localized name
        var inboxName = GetInboxAppName(path, resolvedPath);
        if (inboxName != null)
        {
            _displayNameCache[path] = inboxName;
            return inboxName;
        }

        // 1. Try FileVersionInfo.FileDescription (most reliable for localized names)
        try
        {
            if (File.Exists(resolvedPath))
            {
                var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(resolvedPath);
                if (!string.IsNullOrWhiteSpace(versionInfo.FileDescription))
                {
                    _displayNameCache[path] = versionInfo.FileDescription;
                    return versionInfo.FileDescription;
                }
            }
        }
        catch { }

        // 2. Fallback: SHGetFileInfoW display name
        try
        {
            var shinfo = new SHFILEINFO();
            uint flags = SHGFI_DISPLAYNAME;
            if (!File.Exists(resolvedPath))
                flags |= SHGFI_USEFILEATTRIBUTES;

            var res = SHGetFileInfoW(resolvedPath, 0, ref shinfo,
                (uint)Marshal.SizeOf(shinfo), flags);

            if (res != IntPtr.Zero && !string.IsNullOrWhiteSpace(shinfo.szDisplayName))
            {
                var shellName = StripExtension(shinfo.szDisplayName, resolvedPath);
                _displayNameCache[path] = shellName;
                return shellName;
            }
        }
        catch { }

        // 3. Final fallback: filename without extension
        var fallback = Path.GetFileNameWithoutExtension(path) ?? path;
        _displayNameCache[path] = fallback;
        return fallback;
    }
    /// <summary>
    /// Gets the default browser executable path from Windows registry.
    /// </summary>
    private static string? GetDefaultBrowserPath()
    {
        try
        {
            // Get the ProgId for http protocol
            using var userChoice = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice");
            var progId = userChoice?.GetValue("ProgId") as string;
            if (string.IsNullOrEmpty(progId)) return null;

            // Get the command for that ProgId
            using var commandKey = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(
                $@"{progId}\shell\open\command");
            var command = commandKey?.GetValue(null) as string;
            if (string.IsNullOrEmpty(command)) return null;

            // Extract exe path from command string (e.g. "C:\...\chrome.exe" --args)
            string exePath;
            if (command.StartsWith("\""))
            {
                var endQuote = command.IndexOf('"', 1);
                exePath = endQuote > 1 ? command.Substring(1, endQuote - 1) : command;
            }
            else
            {
                var spaceIdx = command.IndexOf(' ');
                exePath = spaceIdx > 0 ? command.Substring(0, spaceIdx) : command;
            }

            return File.Exists(exePath) ? exePath : null;
        }
        catch
        {
            return null;
        }
    }
}
