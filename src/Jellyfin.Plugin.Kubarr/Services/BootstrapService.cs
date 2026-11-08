using Jellyfin.Data;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities.Security;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Branding;
using MediaBrowser.Model.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kubarr.Services;

/// <summary>
/// Sets Jellyfin up the way Kubarr describes, in place of the startup wizard,
/// and keeps the Kubarr-managed parts in step:
/// <list type="bullet">
/// <item>the API key Sonarr and Radarr use (previously a SQLite-writing sidecar);</item>
/// <item>server name and languages, then the wizard marked complete;</item>
/// <item>media libraries for Kubarr's folders, created once each;</item>
/// <item>hardware transcoding for the GPU Kubarr assigned;</item>
/// <item>a "Sign in with Kubarr" link on the login page.</item>
/// </list>
/// Each step is idempotent and leaves alone what a person changed by hand.
/// </summary>
public class BootstrapService : BackgroundService
{
    public const string ApiKeyName = "Kubarr";

    private const string DisclaimerMarker = "<!-- kubarr-sso -->";

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly KubarrSettings _settings;
    private readonly IServerConfigurationManager _config;
    private readonly ILibraryManager _libraries;
    private readonly IUserManager _users;
    private readonly IDbContextFactory<JellyfinDbContext> _db;
    private readonly ILogger<BootstrapService> _logger;

    public BootstrapService(
        KubarrSettings settings,
        IServerConfigurationManager config,
        ILibraryManager libraries,
        IUserManager users,
        IDbContextFactory<JellyfinDbContext> db,
        ILogger<BootstrapService> logger)
    {
        _settings = settings;
        _config = config;
        _libraries = libraries;
        _users = users;
        _db = db;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let Jellyfin finish starting: the library manager and user store
        // are not ready while hosted services start.
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunStep("API key", EnsureApiKeyAsync, stoppingToken).ConfigureAwait(false);
            await RunStep("startup wizard", CompleteWizardAsync, stoppingToken).ConfigureAwait(false);
            await RunStep("libraries", EnsureLibrariesAsync, stoppingToken).ConfigureAwait(false);
            await RunStep("hardware transcoding", ApplyGpuAsync, stoppingToken).ConfigureAwait(false);
            await RunStep("login page", EnsureLoginLinkAsync, stoppingToken).ConfigureAwait(false);
            await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunStep(string name, Func<CancellationToken, Task> step, CancellationToken ct)
    {
        try
        {
            await step(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Kubarr setup step {Step} failed; retrying later", name);
        }
    }

    private async Task EnsureApiKeyAsync(CancellationToken ct)
    {
        var key = _settings.LibraryKey;
        if (key is null)
        {
            return;
        }

        var db = await _db.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var rows = await db.ApiKeys.Where(k => k.Name == ApiKeyName).ToListAsync(ct).ConfigureAwait(false);
            if (rows.Count == 1 && rows[0].AccessToken == key)
            {
                return;
            }

            db.ApiKeys.RemoveRange(rows);
            db.ApiKeys.Add(new ApiKey(ApiKeyName) { AccessToken = key });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Registered Kubarr's library API key");
        }
    }

    private async Task CompleteWizardAsync(CancellationToken ct)
    {
        var server = _config.Configuration;
        if (server.IsStartupWizardCompleted)
        {
            return;
        }

        var bootstrap = _settings.Bootstrap;
        server.ServerName = bootstrap.ServerName;
        server.UICulture = bootstrap.UiCulture;
        server.PreferredMetadataLanguage = bootstrap.MetadataLanguage;
        server.MetadataCountryCode = bootstrap.MetadataCountry;

        // Jellyfin seeds a passwordless administrator for the wizard to name.
        // Nobody will, and Jellyfin is reachable without a Kubarr session, so
        // lock it behind a random password and hide it. Kubarr administrators
        // become Jellyfin administrators when they first sign in.
        foreach (var user in _users.GetUsers().ToList())
        {
            if (!string.IsNullOrEmpty(user.Password)
                || user.AuthenticationProviderId == Identity.UserLinker.KubarrProviderId)
            {
                continue;
            }

            await _users.ChangePassword(user.Id, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))).ConfigureAwait(false);
            var policy = _users.GetUserDto(user, string.Empty).Policy;
            if (policy is not null)
            {
                policy.IsHidden = true;
                await _users.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
            }

            _logger.LogInformation("Locked Jellyfin's setup account {Name} with a random password", user.Username);
        }

        server.IsStartupWizardCompleted = true;
        _config.SaveConfiguration();
        Plugin.Instance!.Configuration.WizardCompletedByKubarr = true;
        Plugin.Instance.SaveConfiguration();
        _logger.LogInformation("Completed Jellyfin's startup wizard for Kubarr");
    }

    private async Task EnsureLibrariesAsync(CancellationToken ct)
    {
        var config = Plugin.Instance!.Configuration;
        var existing = _libraries.GetVirtualFolders().Select(f => f.Name);
        var toCreate = ServerSetup.LibrariesToCreate(_settings.Bootstrap.Libraries, existing, config.ManagedLibraries, Directory.Exists);
        foreach (var library in toCreate)
        {
            var options = new LibraryOptions
            {
                PathInfos = library.Paths.Select(p => new MediaPathInfo(p)).ToArray(),
                PreferredMetadataLanguage = _config.Configuration.PreferredMetadataLanguage,
                MetadataCountryCode = _config.Configuration.MetadataCountryCode,
                EnableRealtimeMonitor = true,
            };
            await _libraries.AddVirtualFolder(library.Name, ServerSetup.CollectionType(library.Type), options, refreshLibrary: true).ConfigureAwait(false);
            config.ManagedLibraries.Add(library.Name);
            Plugin.Instance.SaveConfiguration();
            _logger.LogInformation("Created library {Name} for {Paths}", library.Name, string.Join(", ", library.Paths));
        }
    }

    private Task ApplyGpuAsync(CancellationToken ct)
    {
        var vendor = _settings.GpuVendor;
        var config = Plugin.Instance!.Configuration;
        if (vendor == config.AppliedGpuVendor)
        {
            // Only act when Kubarr's assignment changes, so a person's own
            // tuning in the dashboard survives restarts.
            return Task.CompletedTask;
        }

        var encoding = (EncodingOptions)_config.GetConfiguration("encoding");
        encoding.HardwareAccelerationType = ServerSetup.AccelerationFor(vendor);
        encoding.EnableHardwareEncoding = encoding.HardwareAccelerationType != MediaBrowser.Model.Entities.HardwareAccelerationType.none;
        if (vendor is "intel" or "amd")
        {
            encoding.VaapiDevice = ServerSetup.RenderNode;
        }

        _config.SaveConfiguration("encoding", encoding);
        config.AppliedGpuVendor = vendor;
        Plugin.Instance.SaveConfiguration();
        _logger.LogInformation("Hardware transcoding set to {Type} for GPU vendor '{Vendor}'", encoding.HardwareAccelerationType, vendor);
        return Task.CompletedTask;
    }

    private Task EnsureLoginLinkAsync(CancellationToken ct)
    {
        var branding = (BrandingOptions)_config.GetConfiguration("branding");
        var current = branding.LoginDisclaimer ?? string.Empty;
        if (current.Length > 0 && !current.Contains(DisclaimerMarker, StringComparison.Ordinal))
        {
            return Task.CompletedTask; // A person wrote their own; leave it.
        }

        var wanted = DisclaimerMarker + "<a href=\"../Kubarr/sso\" style=\"display:block;text-align:center;font-weight:600;margin-top:1em\">Sign in with Kubarr</a>";
        if (current == wanted)
        {
            return Task.CompletedTask;
        }

        branding.LoginDisclaimer = wanted;
        _config.SaveConfiguration("branding", branding);
        return Task.CompletedTask;
    }
}
