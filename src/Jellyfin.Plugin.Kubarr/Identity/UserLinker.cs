using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kubarr.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kubarr.Identity;

/// <summary>
/// Applies Kubarr's view of a user to Jellyfin's accounts: creates accounts
/// at first sign-in, keeps name and administrator flag in step, and disables
/// and signs out accounts whose user lost access.
/// </summary>
public class UserLinker
{
    /// <summary>Jellyfin records a provider by its type's full name.</summary>
    public static readonly string KubarrProviderId = typeof(KubarrAuthenticationProvider).FullName!;

    private const string FallbackDefaultProviderId = "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly IUserManager _users;
    private readonly ISessionManager _sessions;
    private readonly ILogger<UserLinker> _logger;

    public UserLinker(IUserManager users, ISessionManager sessions, ILogger<UserLinker> logger)
    {
        _users = users;
        _sessions = sessions;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    private string DefaultProviderId =>
        _users.GetAuthenticationProviders()
            .FirstOrDefault(p => string.Equals(p.Name, "Default", StringComparison.OrdinalIgnoreCase))?.Id
        ?? FallbackDefaultProviderId;

    /// <summary>
    /// The Jellyfin account for a verified Kubarr user, creating it when
    /// <paramref name="create"/> is set. Null when the name belongs to an
    /// account another provider owns.
    /// </summary>
    public async Task<User?> EnsureUserAsync(KubarrUser kubarrUser, bool create)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var user = LinkedUser(kubarrUser.UserId);
            if (user is null)
            {
                user = _users.GetUserByName(kubarrUser.Username);
                if (user is not null)
                {
                    if (LinkedTo(user.Id) is { } other && other != kubarrUser.UserId)
                    {
                        _logger.LogWarning("Jellyfin account {Name} belongs to another Kubarr user", user.Username);
                        return null;
                    }

                    if (!SyncPlanner.Adoptable(Account(user), KubarrProviderId, DefaultProviderId))
                    {
                        _logger.LogWarning("Jellyfin account {Name} is owned by provider {Provider}; not adopting", user.Username, user.AuthenticationProviderId);
                        return null;
                    }
                }
                else if (create)
                {
                    user = await _users.CreateUserAsync(kubarrUser.Username).ConfigureAwait(false);
                    _logger.LogInformation("Created Jellyfin account {Name} for Kubarr user {Id}", user.Username, kubarrUser.UserId);
                }
                else
                {
                    return null;
                }

                SetLink(kubarrUser.UserId, user.Id);
            }

            if (!string.Equals(user.Username, kubarrUser.Username, StringComparison.Ordinal)
                && _users.GetUserByName(kubarrUser.Username) is null)
            {
                await _users.RenameUser(user.Id, user.Username, kubarrUser.Username).ConfigureAwait(false);
            }

            await ApplyPolicyAsync(user.Id, kubarrUser.IsAdmin, !kubarrUser.Enabled).ConfigureAwait(false);
            return _users.GetUserById(user.Id);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>The Kubarr user id linked to this account, if any.</summary>
    public string? LinkedTo(Guid jellyfinUserId) =>
        Config.Links.FirstOrDefault(l => l.JellyfinUserId == jellyfinUserId)?.KubarrUserId;

    /// <summary>Bring every Kubarr-owned account in line with a snapshot.</summary>
    public async Task ApplySnapshotAsync(IdentitySnapshot snapshot)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var accounts = _users.GetUsers().Select(Account).ToList();
            var actions = SyncPlanner.Plan(Config.Links.ToList(), accounts, snapshot, KubarrProviderId, DefaultProviderId);
            foreach (var action in actions)
            {
                try
                {
                    await ApplyAsync(action).ConfigureAwait(false);
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                {
                    // Jellyfin refuses to demote or disable its last administrator.
                    _logger.LogWarning(e, "Could not apply {Action}", action);
                }
            }

            if (actions.Count > 0)
            {
                _logger.LogInformation("Applied {Count} Kubarr user changes", actions.Count);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task ApplyAsync(SyncAction action)
    {
        switch (action)
        {
            case SyncAction.Link link:
                SetLink(link.KubarrUserId, link.JellyfinUserId);
                break;
            case SyncAction.Unlink unlink:
                Config.Links.RemoveAll(l => l.KubarrUserId == unlink.KubarrUserId);
                Plugin.Instance!.SaveConfiguration();
                break;
            case SyncAction.Rename rename:
                await _users.RenameUser(rename.JellyfinUserId, rename.From, rename.To).ConfigureAwait(false);
                break;
            case SyncAction.SetPolicy policy:
                await ApplyPolicyAsync(policy.JellyfinUserId, policy.IsAdmin, policy.IsDisabled).ConfigureAwait(false);
                break;
            case SyncAction.RevokeSessions revoke:
                await _sessions.RevokeUserTokens(revoke.JellyfinUserId, null!).ConfigureAwait(false);
                break;
        }
    }

    private async Task ApplyPolicyAsync(Guid userId, bool isAdmin, bool isDisabled)
    {
        var user = _users.GetUserById(userId);
        if (user is null)
        {
            return;
        }

        var policy = _users.GetUserDto(user, string.Empty).Policy;
        if (policy is null)
        {
            return;
        }

        if (policy.IsAdministrator == isAdmin && policy.IsDisabled == isDisabled
            && policy.AuthenticationProviderId == KubarrProviderId)
        {
            return;
        }

        policy.IsAdministrator = isAdmin;
        policy.IsDisabled = isDisabled;
        policy.AuthenticationProviderId = KubarrProviderId;
        await _users.UpdatePolicyAsync(userId, policy).ConfigureAwait(false);
    }

    private User? LinkedUser(string kubarrUserId)
    {
        var link = Config.Links.FirstOrDefault(l => l.KubarrUserId == kubarrUserId);
        if (link is null)
        {
            return null;
        }

        var user = _users.GetUserById(link.JellyfinUserId);
        if (user is null)
        {
            Config.Links.Remove(link);
            Plugin.Instance!.SaveConfiguration();
        }

        return user;
    }

    private static void SetLink(string kubarrUserId, Guid jellyfinUserId)
    {
        Config.Links.RemoveAll(l => l.KubarrUserId == kubarrUserId || l.JellyfinUserId == jellyfinUserId);
        Config.Links.Add(new UserLink { KubarrUserId = kubarrUserId, JellyfinUserId = jellyfinUserId });
        Plugin.Instance!.SaveConfiguration();
    }

    private static JellyfinAccount Account(User user) => new(
        user.Id,
        user.Username,
        user.AuthenticationProviderId,
        user.HasPermission(PermissionKind.IsAdministrator),
        user.HasPermission(PermissionKind.IsDisabled));
}
