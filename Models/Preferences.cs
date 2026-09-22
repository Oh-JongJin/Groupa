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
}
