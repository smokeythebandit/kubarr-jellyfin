using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kubarr.Identity;
using MediaBrowser.Controller.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kubarr;

/// <summary>
/// Checks a username and password against Kubarr. This is how native clients
/// (TV, phone) sign in, since they cannot use Kubarr's browser session. A
/// Kubarr user with two-factor authentication signs in with an app password.
/// </summary>
public class KubarrAuthenticationProvider : IAuthenticationProvider, IRequiresResolvedUser
{
    private readonly KubarrClient _kubarr;
    private readonly IServiceProvider _services;
    private readonly ILogger<KubarrAuthenticationProvider> _logger;

    /// <summary>
    /// Takes the container rather than <see cref="UserLinker"/>: Jellyfin's
    /// user manager is built from the sign-in providers, and the linker needs
    /// the user manager, so asking for it here would be a cycle.
    /// </summary>
    public KubarrAuthenticationProvider(KubarrClient kubarr, IServiceProvider services, ILogger<KubarrAuthenticationProvider> logger)
    {
        _kubarr = kubarr;
        _services = services;
        _logger = logger;
    }

    public string Name => "Kubarr";

    public bool IsEnabled => _kubarr.IsConfigured;

    private UserLinker Linker => _services.GetRequiredService<UserLinker>();

    /// <summary>
    /// No Jellyfin account matches the name. Jellyfin 12 expects the provider
    /// to create it, so the linker creates, links and sets the role here;
    /// Jellyfin then finds it under the returned name.
    /// </summary>
    public async Task<ProviderAuthenticationResult> Authenticate(string username, string password)
    {
        var user = await VerifyAsync(username, password).ConfigureAwait(false);
        var account = await Linker.EnsureUserAsync(user, create: true).ConfigureAwait(false)
            ?? throw new AuthenticationException("Invalid username or password");
        return new ProviderAuthenticationResult { Username = account.Username, DisplayName = user.Username };
    }

    /// <summary>The account exists; it must belong to the Kubarr user who signed in.</summary>
    public async Task<ProviderAuthenticationResult> Authenticate(string username, string password, User? resolvedUser)
    {
        if (resolvedUser is null)
        {
            return await Authenticate(username, password).ConfigureAwait(false);
        }

        var user = await VerifyAsync(username, password).ConfigureAwait(false);
        var linked = Linker.LinkedTo(resolvedUser.Id);
        if (linked is not null && linked != user.UserId)
        {
            _logger.LogWarning("Kubarr user {Id} tried to sign in to Jellyfin account {Name} owned by another", user.UserId, resolvedUser.Username);
            throw new AuthenticationException("Invalid username or password");
        }

        // Jellyfin holds this account's lock until sign-in finishes, and
        // updating its policy takes the same lock, so bring name and role in
        // step just after.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            try
            {
                await Linker.EnsureUserAsync(user, create: false).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Could not update Jellyfin account for Kubarr user {Id}", user.UserId);
            }
        });
        return new ProviderAuthenticationResult { Username = resolvedUser.Username, DisplayName = user.Username };
    }

    public Task ChangePassword(User user, string newPassword) =>
        throw new NotSupportedException("Change your password in Kubarr.");

    private async Task<KubarrUser> VerifyAsync(string username, string password)
    {
        var (outcome, user) = await _kubarr.VerifyAsync(username, password).ConfigureAwait(false);
        return outcome switch
        {
            VerifyOutcome.Verified => user!,
            VerifyOutcome.Rejected => throw new AuthenticationException("Invalid username or password"),
            _ => throw new AuthenticationException("Kubarr is unavailable; try again shortly"),
        };
    }
}
