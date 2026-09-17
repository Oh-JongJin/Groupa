using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace JumpListLauncher.Models;

public class AppConfig
{
    [JsonPropertyName("group_name")]
    public string GroupName { get; set; } = "Groupa";

    [JsonPropertyName("apps")]
    public List<AppEntry> Apps { get; set; } = new();
}

public class AppEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";
}
