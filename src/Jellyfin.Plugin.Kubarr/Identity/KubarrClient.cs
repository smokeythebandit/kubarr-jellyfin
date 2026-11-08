using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kubarr.Identity;

public enum VerifyOutcome
{
    Verified,
    Rejected,
    Unavailable,
}

/// <summary>Calls Kubarr's integration API with the identity key.</summary>
public class KubarrClient
{
    private static readonly TimeSpan KeyCacheLifetime = TimeSpan.FromMinutes(5);

    private readonly KubarrSettings _settings;
    private readonly ILogger<KubarrClient> _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly SemaphoreSlim _keyLock = new(1, 1);
    private IReadOnlyList<JsonWebKey> _keys = Array.Empty<JsonWebKey>();
    private DateTimeOffset _keysFetched = DateTimeOffset.MinValue;

    public KubarrClient(KubarrSettings settings, ILogger<KubarrClient> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Whether Kubarr gave this pod its identity key.</summary>
    public bool IsConfigured => _settings.IdentityKey is not null;

    public async Task<(VerifyOutcome Outcome, KubarrUser? User)> VerifyAsync(string username, string password, CancellationToken ct = default)
    {
        using var request = Authorized(HttpMethod.Post, "api/integrations/jellyfin/verify");
        if (request is null)
        {
            return (VerifyOutcome.Unavailable, null);
        }

        // Buffered, so the request carries a Content-Length rather than chunking.
        request.Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(new { username, password }),
            System.Text.Encoding.UTF8,
            "application/json");
        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var user = await response.Content.ReadFromJsonAsync<KubarrUser>(ct).ConfigureAwait(false);
                return user is null ? (VerifyOutcome.Unavailable, null) : (VerifyOutcome.Verified, user);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                return (VerifyOutcome.Rejected, null);
            }

            _logger.LogWarning("Kubarr answered {Status} to a sign-in check", (int)response.StatusCode);
            return (VerifyOutcome.Unavailable, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(e, "Kubarr unreachable for a sign-in check");
            return (VerifyOutcome.Unavailable, null);
        }
    }

    public async Task<IdentitySnapshot?> GetSnapshotAsync(CancellationToken ct = default)
    {
        using var request = Authorized(HttpMethod.Get, "api/integrations/jellyfin/users");
        if (request is null)
        {
            return null;
        }

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Kubarr answered {Status} to a user list request", (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<IdentitySnapshot>(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(e, "Could not read Kubarr's user list");
            return null;
        }
    }

    /// <summary>Kubarr's signing keys, cached; <paramref name="refresh"/> refetches.</summary>
    public async Task<IReadOnlyList<JsonWebKey>> GetSigningKeysAsync(bool refresh, CancellationToken ct = default)
    {
        if (!refresh && DateTimeOffset.UtcNow - _keysFetched < KeyCacheLifetime && _keys.Count > 0)
        {
            return _keys;
        }

        await _keyLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!refresh && DateTimeOffset.UtcNow - _keysFetched < KeyCacheLifetime && _keys.Count > 0)
            {
                return _keys;
            }

            var set = await _http.GetFromJsonAsync<JsonWebKeySet>(new Uri(_settings.ApiUrl, "auth/jwks"), ct).ConfigureAwait(false);
            _keys = set?.Keys ?? new List<JsonWebKey>();
            _keysFetched = DateTimeOffset.UtcNow;
            return _keys;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(e, "Could not fetch Kubarr's signing keys");
            return _keys;
        }
        finally
        {
            _keyLock.Release();
        }
    }

    private HttpRequestMessage? Authorized(HttpMethod method, string path)
    {
        var key = _settings.IdentityKey;
        if (key is null)
        {
            return null;
        }

        var request = new HttpRequestMessage(method, new Uri(_settings.ApiUrl, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }
}
