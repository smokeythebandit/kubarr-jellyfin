using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Kubarr;

/// <summary>
/// What Kubarr tells the plugin, through the environment and files the
/// Jellyfin chart mounts. Read on demand so a rotated Secret or edited
/// ConfigMap is picked up without a restart.
/// </summary>
public class KubarrSettings
{
    private readonly Func<string, string?> _env;

    public KubarrSettings()
        : this(Environment.GetEnvironmentVariable)
    {
    }

    internal KubarrSettings(Func<string, string?> env)
    {
        _env = env;
    }

    /// <summary>Kubarr's API inside the cluster.</summary>
    public Uri ApiUrl => new(Get("KUBARR_API_URL", "http://kubarr-backend.kubarr-backend.svc.cluster.local:8000").TrimEnd('/') + "/");

    /// <summary>
    /// Key both sides use to call each other. Only Jellyfin's namespace holds
    /// it; without it the plugin stays dormant.
    /// </summary>
    public string? IdentityKey => ReadSecret(Get("KUBARR_IDENTITY_KEY_FILE", "/kubarr-secrets/jellyfin-identity/api-key"));

    /// <summary>Key Sonarr and Radarr use to refresh libraries; the plugin registers it.</summary>
    public string? LibraryKey => ReadSecret(Get("KUBARR_LIBRARY_KEY_FILE", "/kubarr-secrets/jellyfin/api-key"));

    /// <summary>GPU vendor Kubarr assigned to this pod: intel, amd, nvidia or empty.</summary>
    public string GpuVendor => Get("KUBARR_GPU_VENDOR", string.Empty).Trim().ToLowerInvariant();

    public BootstrapSettings Bootstrap
    {
        get
        {
            var path = Get("KUBARR_BOOTSTRAP_FILE", "/kubarr-config/bootstrap.json");
            try
            {
                return File.Exists(path)
                    ? JsonSerializer.Deserialize<BootstrapSettings>(File.ReadAllText(path), JsonOptions) ?? new BootstrapSettings()
                    : new BootstrapSettings();
            }
            catch (JsonException)
            {
                return new BootstrapSettings();
            }
        }
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private string Get(string name, string fallback)
    {
        var value = _env(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string? ReadSecret(string path)
    {
        try
        {
            var value = File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Server setup Kubarr wants, from the chart's bootstrap ConfigMap.</summary>
public class BootstrapSettings
{
    public string ServerName { get; set; } = "Kubarr";

    public string UiCulture { get; set; } = "en-US";

    public string MetadataLanguage { get; set; } = "en";

    public string MetadataCountry { get; set; } = "US";

    public List<LibrarySpec> Libraries { get; set; } = new();
}

public class LibrarySpec
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Jellyfin collection type: movies, tvshows, music, ...</summary>
    public string Type { get; set; } = string.Empty;

    public List<string> Paths { get; set; } = new();
}
