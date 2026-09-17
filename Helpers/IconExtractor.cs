using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace JumpListLauncher.Helpers;

public static class IconExtractor
{
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

    public static BitmapSource? ExtractIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        // Skip URLs — no icon to extract
        if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return null;

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

        // Try resolving short paths
        string resolvedPath = path;
        if (!File.Exists(path) && !Path.IsPathRooted(path))
        {
            var found = FindInPath(path);
            if (found != null) resolvedPath = found;
        }

        // 1. Try FileVersionInfo.FileDescription (most reliable for localized names)
        try
        {
            if (File.Exists(resolvedPath))
            {
                var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(resolvedPath);
                if (!string.IsNullOrWhiteSpace(versionInfo.FileDescription))
                    return versionInfo.FileDescription;
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
                return shinfo.szDisplayName;
        }
        catch { }

        // 3. Final fallback: filename without extension
        return Path.GetFileNameWithoutExtension(path) ?? path;
    }
}
