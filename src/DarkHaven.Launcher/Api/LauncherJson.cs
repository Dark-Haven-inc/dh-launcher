using System.Text.Json;

namespace DarkHaven.Launcher.Api;

/// <summary>Shared <see cref="JsonSerializerOptions"/> for all launcher HTTP APIs.</summary>
public static class LauncherJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
