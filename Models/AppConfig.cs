using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace JumpListLauncher.Models;

public class AppConfig
{
    [JsonPropertyName("groups")]
    public List<AppGroup> Groups { get; set; } = new();

    // Legacy fields for migration (old single-group format)
    [JsonPropertyName("group_name")]
    public string? GroupName { get; set; }

    [JsonPropertyName("apps")]
    public List<AppEntry>? Apps { get; set; }

    /// <summary>
    /// Migrate old single-group format to new multi-group format.
    /// </summary>
    public void MigrateIfNeeded()
    {
        if (Groups.Count == 0 && Apps != null && Apps.Count > 0)
        {
            Groups.Add(new AppGroup
            {
                GroupName = GroupName ?? "Groupa",
                Apps = Apps
            });
            GroupName = null;
            Apps = null;
        }

        if (Groups.Count == 0)
        {
            Groups.Add(new AppGroup { GroupName = "Groupa" });
        }
    }
}

public class AppGroup
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
