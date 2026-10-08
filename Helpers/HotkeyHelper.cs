using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace JumpListLauncher.Helpers;

public static class HotkeyHelper
{
    private const int WM_HOTKEY = 0x0312;
    public const int HOTKEY_ID = 9000;

    // Modifiers
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CTRL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private static IntPtr _hwnd;
    private static HwndSource? _source;
    private static Action? _callback;

    public static bool Register(System.Windows.Window hiddenWindow, string modifiers, string key, Action onHotkey)
    {
        _callback = onHotkey;
        var helper = new WindowInteropHelper(hiddenWindow);
        helper.EnsureHandle();
        _hwnd = helper.Handle;

        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        uint mod = ParseModifiers(modifiers) | MOD_NOREPEAT;
        uint vk = ParseKey(key);

        return RegisterHotKey(_hwnd, HOTKEY_ID, mod, vk);
    }

    public static void Unregister()
    {
        _source?.RemoveHook(WndProc);
        if (_hwnd != IntPtr.Zero)
            UnregisterHotKey(_hwnd, HOTKEY_ID);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            _callback?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public static uint ParseModifiers(string modifiers)
    {
        uint mod = 0;
        var upper = modifiers.ToUpperInvariant();
        if (upper.Contains("CTRL")) mod |= MOD_CTRL;
        if (upper.Contains("ALT")) mod |= MOD_ALT;
        if (upper.Contains("SHIFT")) mod |= MOD_SHIFT;
        if (upper.Contains("WIN")) mod |= MOD_WIN;
        return mod;
    }

    public static uint ParseKey(string key)
    {
        // Try to parse as Key enum first
        if (Enum.TryParse<Key>(key, true, out var wpfKey))
        {
            return (uint)KeyInterop.VirtualKeyFromKey(wpfKey);
        }

        // Single character
        if (key.Length == 1)
        {
            return (uint)char.ToUpperInvariant(key[0]);
        }

        // Default: G
        return 0x47;
    }

    /// <summary>
    /// Format a hotkey display string like "Ctrl+Shift+G"
    /// </summary>
    public static string FormatHotkey(string modifiers, string key)
    {
        return $"{modifiers}+{key}";
    }
}
