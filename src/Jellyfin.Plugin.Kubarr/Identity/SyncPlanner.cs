using Jellyfin.Plugin.Kubarr.Configuration;

namespace Jellyfin.Plugin.Kubarr.Identity;

/// <summary>A Jellyfin account as the planner sees it.</summary>
public record JellyfinAccount(Guid Id, string Username, string AuthenticationProviderId, bool IsAdmin, bool IsDisabled);

public abstract record SyncAction
{
    /// <summary>Record that this Kubarr user owns the account, switching it to the Kubarr provider.</summary>
    public sealed record Link(string KubarrUserId, Guid JellyfinUserId) : SyncAction;

    /// <summary>Forget a link whose Jellyfin account was deleted.</summary>
    public sealed record Unlink(string KubarrUserId) : SyncAction;

    public sealed record Rename(Guid JellyfinUserId, string From, string To) : SyncAction;

    public sealed record SetPolicy(Guid JellyfinUserId, bool IsAdmin, bool IsDisabled) : SyncAction;

    /// <summary>End every session of an account that just lost access.</summary>
    public sealed record RevokeSessions(Guid JellyfinUserId) : SyncAction;
}

/// <summary>
/// Decides what a Kubarr snapshot means for Jellyfin's accounts. Pure, so the
/// rules are tested without a server:
/// <list type="bullet">
/// <item>Accounts are created only at sign-in (just in time), never here.</item>
/// <item>A linked account follows its Kubarr user: name, administrator, enabled.</item>
/// <item>A linked account whose user left the snapshot is disabled and signed out.</item>
/// <item>An unlinked account with a Kubarr user's name is adopted, unless another
/// provider (LDAP, SSO) owns it.</item>
/// <item>Accounts Kubarr never linked are not touched: a local break-glass admin survives.</item>
/// </list>
/// </summary>
public static class SyncPlanner
{
    public static List<SyncAction> Plan(
        IReadOnlyList<UserLink> links,
        IReadOnlyList<JellyfinAccount> accounts,
        IdentitySnapshot snapshot,
        string kubarrProviderId,
        string defaultProviderId)
    {
        var actions = new List<SyncAction>();
        var byId = accounts.ToDictionary(a => a.Id);
        var wanted = snapshot.Users
            .GroupBy(u => u.UserId)
            .ToDictionary(g => g.Key, g => g.First());
        var linkedKubarr = new HashSet<string>();
        var linkedJellyfin = new HashSet<Guid>();

        foreach (var link in links)
        {
            if (!byId.TryGetValue(link.JellyfinUserId, out var account))
            {
                actions.Add(new SyncAction.Unlink(link.KubarrUserId));
                continue;
            }

            linkedKubarr.Add(link.KubarrUserId);
            linkedJellyfin.Add(account.Id);

            if (wanted.TryGetValue(link.KubarrUserId, out var user))
            {
                if (!string.Equals(account.Username, user.Username, StringComparison.Ordinal)
                    && !accounts.Any(a => a.Id != account.Id && string.Equals(a.Username, user.Username, StringComparison.OrdinalIgnoreCase)))
                {
                    actions.Add(new SyncAction.Rename(account.Id, account.Username, user.Username));
                }

                var disabled = !user.Enabled;
                if (account.IsAdmin != user.IsAdmin || account.IsDisabled != disabled
                    || account.AuthenticationProviderId != kubarrProviderId)
                {
                    actions.Add(new SyncAction.SetPolicy(account.Id, user.IsAdmin, disabled));
                }

                if (disabled && !account.IsDisabled)
                {
                    actions.Add(new SyncAction.RevokeSessions(account.Id));
                }
            }
            else if (!account.IsDisabled)
            {
                actions.Add(new SyncAction.SetPolicy(account.Id, false, true));
                actions.Add(new SyncAction.RevokeSessions(account.Id));
            }
        }

        foreach (var user in wanted.Values.Where(u => !linkedKubarr.Contains(u.UserId)))
        {
            var match = accounts.FirstOrDefault(a =>
                !linkedJellyfin.Contains(a.Id)
                && string.Equals(a.Username, user.Username, StringComparison.OrdinalIgnoreCase));
            if (match is null || !Adoptable(match, kubarrProviderId, defaultProviderId))
            {
                continue;
            }

            linkedJellyfin.Add(match.Id);
            actions.Add(new SyncAction.Link(user.UserId, match.Id));
            actions.Add(new SyncAction.SetPolicy(match.Id, user.IsAdmin, !user.Enabled));
        }

        return actions;
    }

    /// <summary>Whether an unlinked account may be claimed for a Kubarr user of the same name.</summary>
    public static bool Adoptable(JellyfinAccount account, string kubarrProviderId, string defaultProviderId) =>
        account.AuthenticationProviderId == kubarrProviderId
        || account.AuthenticationProviderId == defaultProviderId
        || string.IsNullOrEmpty(account.AuthenticationProviderId);
}
