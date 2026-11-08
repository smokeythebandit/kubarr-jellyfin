using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Kubarr.Identity;

/// <summary>A Kubarr user as Jellyfin should see them.</summary>
public record KubarrUser
{
    [JsonPropertyName("user_id")]
    public string UserId { get; init; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; init; } = string.Empty;

    [JsonPropertyName("is_admin")]
    public bool IsAdmin { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;
}

public record IdentitySnapshot
{
    [JsonPropertyName("users")]
    public List<KubarrUser> Users { get; init; } = new();
}
