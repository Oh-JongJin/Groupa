using System.Text.Json.Serialization;

namespace JumpListLauncher.Models;

public class Preferences
{
    /// <summary>
    /// Theme: "system", "dark", "light"
    /// </summary>
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "system";

    /// <summary>
    /// Language: "system", "ko", "en"
    /// </summary>
    [JsonPropertyName("language")]
    public string Language { get; set; } = "system";

    /// <summary>
    /// Enable global hotkey + system tray background mode
    /// </summary>
    [JsonPropertyName("hotkey_enabled")]
    public bool HotkeyEnabled { get; set; } = false;

    /// <summary>
    /// Hotkey modifier keys: "Ctrl+Shift", "Ctrl+Alt", etc.
    /// </summary>
    [JsonPropertyName("hotkey_modifiers")]
    public string HotkeyModifiers { get; set; } = "Ctrl+Shift";

    /// <summary>
    /// Hotkey key: "G", "F1", "Space", etc.
    /// </summary>
    [JsonPropertyName("hotkey_key")]
    public string HotkeyKey { get; set; } = "G";

    /// <summary>
    /// Where the popup opens when not opened from the taskbar (hotkey etc.):
    /// "taskbar" = above Groupa's taskbar button, "cursor" = at the mouse cursor.
    /// </summary>
    [JsonPropertyName("popup_position")]
    public string PopupPosition { get; set; } = "taskbar";
}
