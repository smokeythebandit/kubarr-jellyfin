using System.Net.Mime;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Kubarr.Identity;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kubarr.Api;

/// <summary>
/// Single sign-on from Kubarr, and the endpoint Kubarr pushes user changes to.
/// Every action is anonymous to Jellyfin: each proves its caller another way
/// (a Kubarr-signed identity token, or the shared identity key).
/// </summary>
[ApiController]
[Route("Kubarr")]
[AllowAnonymous]
public class KubarrController : ControllerBase
{
    /// <summary>Set by Kubarr's gateway for a signed-in Kubarr user; never trusted unsigned.</summary>
    public const string IdentityHeader = "X-Kubarr-Identity";

    /// <summary>
    /// The SSO page sends this so a cross-site form or image cannot start a
    /// session: a custom header forces a CORS preflight browsers will not pass.
    /// </summary>
    public const string SsoRequestHeader = "X-Kubarr-Sso";

    /// <summary>Kubarr authenticates its pushes with this header.</summary>
    public const string KeyHeader = "X-Kubarr-Key";

    private static readonly Lazy<string> SsoPage = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Jellyfin.Plugin.Kubarr.Web.sso.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private readonly KubarrClient _kubarr;
    private readonly KubarrSettings _settings;
    private readonly UserLinker _linker;
    private readonly ISessionManager _sessions;
    private readonly ILogger<KubarrController> _logger;

    public KubarrController(
        KubarrClient kubarr,
        KubarrSettings settings,
        UserLinker linker,
        ISessionManager sessions,
        ILogger<KubarrController> logger)
    {
        _kubarr = kubarr;
        _settings = settings;
        _linker = linker;
        _sessions = sessions;
        _logger = logger;
    }

    /// <summary>
    /// The page Kubarr sends browsers to. It asks <see cref="CreateSession"/>
    /// for a token, stores it the way Jellyfin's web client does, and opens
    /// the web client signed in.
    /// </summary>
    [HttpGet("sso")]
    [Produces(MediaTypeNames.Text.Html)]
    public ContentResult SsoPageResult()
    {
        Response.Headers.CacheControl = "no-store";
        return Content(SsoPage.Value, MediaTypeNames.Text.Html, Encoding.UTF8);
    }

    /// <summary>
    /// Exchange the gateway's identity token for a Jellyfin session. Reuses
    /// the browser's current token when it already belongs to this user, so
    /// opening Jellyfin from Kubarr repeatedly does not pile up devices.
    /// </summary>
    [HttpPost("sso/session")]
    [Consumes(MediaTypeNames.Application.Json)]
    public async Task<ActionResult<SsoSession>> CreateSession([FromBody] SsoSessionRequest request)
    {
        if (!Request.Headers.ContainsKey(SsoRequestHeader))
        {
            return BadRequest();
        }

        var token = Request.Headers[IdentityHeader].ToString();
        if (string.IsNullOrEmpty(token))
        {
            return Unauthorized(new { error = "not_signed_in_to_kubarr" });
        }

        var kubarrUser = await ValidateAsync(token).ConfigureAwait(false);
        if (kubarrUser is null)
        {
            return Unauthorized(new { error = "invalid_identity" });
        }

        var user = await _linker.EnsureUserAsync(kubarrUser, create: true).ConfigureAwait(false);
        if (user is null)
        {
            return Conflict(new { error = "account_owned_elsewhere" });
        }

        var deviceId = Bounded(request.DeviceId, 256) ?? Guid.NewGuid().ToString("N");
        var remote = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;

        if (!string.IsNullOrEmpty(request.CurrentToken))
        {
            try
            {
                var existing = await _sessions.GetSessionByAuthenticationToken(request.CurrentToken, deviceId, remote).ConfigureAwait(false);
                if (existing is not null && existing.UserId == user.Id)
                {
                    return new SsoSession(request.CurrentToken, user.Id.ToString("N"), existing.ServerId);
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or ArgumentException)
            {
                // Stale or foreign token: fall through to a fresh session.
            }
        }

        var result = await _sessions.AuthenticateDirect(new AuthenticationRequest
        {
            UserId = user.Id,
            Username = user.Username,
            App = "Jellyfin Web",
            AppVersion = Bounded(request.AppVersion, 64) ?? "kubarr-sso",
            DeviceId = deviceId,
            DeviceName = Bounded(request.DeviceName, 128) ?? "Browser (Kubarr)",
            RemoteEndPoint = remote,
        }).ConfigureAwait(false);

        _logger.LogInformation("Kubarr user {Id} signed in to Jellyfin as {Name}", kubarrUser.UserId, user.Username);
        return new SsoSession(result.AccessToken, user.Id.ToString("N"), result.ServerId);
    }

    /// <summary>Kubarr pushes its user list here whenever users or roles change.</summary>
    [HttpPost("users/sync")]
    [Consumes(MediaTypeNames.Application.Json)]
    public async Task<IActionResult> Sync([FromBody] IdentitySnapshot snapshot)
    {
        if (!KeyMatches(Request.Headers[KeyHeader].ToString()))
        {
            return Unauthorized();
        }

        await _linker.ApplySnapshotAsync(snapshot).ConfigureAwait(false);
        return NoContent();
    }

    private async Task<KubarrUser?> ValidateAsync(string token)
    {
        var keys = await _kubarr.GetSigningKeysAsync(refresh: false).ConfigureAwait(false);
        var user = IdentityTokenValidator.Validate(token, keys, DateTimeOffset.UtcNow, out var failure);
        if (user is null && failure is "unknown signing key" or "bad signature")
        {
            // Kubarr may have rotated its key since the cache was filled.
            keys = await _kubarr.GetSigningKeysAsync(refresh: true).ConfigureAwait(false);
            user = IdentityTokenValidator.Validate(token, keys, DateTimeOffset.UtcNow, out failure);
        }

        if (user is null)
        {
            _logger.LogWarning("Rejected Kubarr identity token: {Reason}", failure);
        }

        return user;
    }

    private bool KeyMatches(string presented)
    {
        var expected = _settings.IdentityKey;
        if (expected is null || string.IsNullOrEmpty(presented))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(presented.Trim()));
    }

    private static string? Bounded(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length > max ? value[..max] : value;
}

public record SsoSessionRequest(string? DeviceId, string? DeviceName, string? AppVersion, string? CurrentToken);

public record SsoSession(string AccessToken, string UserId, string ServerId);
