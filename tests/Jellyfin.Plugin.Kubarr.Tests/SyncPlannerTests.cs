using Jellyfin.Plugin.Kubarr.Configuration;
using Jellyfin.Plugin.Kubarr.Identity;
using Xunit;

namespace Jellyfin.Plugin.Kubarr.Tests;

public class SyncPlannerTests
{
    private const string Kubarr = "Jellyfin.Plugin.Kubarr.KubarrAuthenticationProvider";
    private const string Default = "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider";
    private const string Ldap = "Jellyfin.Plugin.LDAP_Auth.LdapAuthenticationProviderPlugin";

    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();

    private static KubarrUser U(string id, string name, bool admin = false, bool enabled = true) =>
        new() { UserId = id, Username = name, IsAdmin = admin, Enabled = enabled };

    private static List<SyncAction> Plan(IEnumerable<UserLink> links, IEnumerable<JellyfinAccount> accounts, params KubarrUser[] users) =>
        SyncPlanner.Plan(links.ToList(), accounts.ToList(), new IdentitySnapshot { Users = users.ToList() }, Kubarr, Default);

    private static UserLink L(string kubarrId, Guid id) => new() { KubarrUserId = kubarrId, JellyfinUserId = id };

    [Fact]
    public void LinkedAccountFollowsNameAndRole()
    {
        var actions = Plan(
            new[] { L("1", A) },
            new[] { new JellyfinAccount(A, "alice", Kubarr, false, false) },
            U("1", "alice2", admin: true));
        Assert.Contains(new SyncAction.Rename(A, "alice", "alice2"), actions);
        Assert.Contains(new SyncAction.SetPolicy(A, true, false), actions);
    }

    [Fact]
    public void InSyncAccountNeedsNothing()
    {
        var actions = Plan(new[] { L("1", A) }, new[] { new JellyfinAccount(A, "alice", Kubarr, false, false) }, U("1", "alice"));
        Assert.Empty(actions);
    }

    [Fact]
    public void UserWhoLostAccessIsDisabledAndSignedOut()
    {
        var actions = Plan(new[] { L("1", A) }, new[] { new JellyfinAccount(A, "alice", Kubarr, true, false) });
        Assert.Equal(new SyncAction[] { new SyncAction.SetPolicy(A, false, true), new SyncAction.RevokeSessions(A) }, actions);
    }

    [Fact]
    public void DisabledKubarrAccountIsDisabledAndSignedOut()
    {
        var actions = Plan(new[] { L("1", A) }, new[] { new JellyfinAccount(A, "alice", Kubarr, false, false) }, U("1", "alice", enabled: false));
        Assert.Contains(new SyncAction.SetPolicy(A, false, true), actions);
        Assert.Contains(new SyncAction.RevokeSessions(A), actions);
    }

    [Fact]
    public void ExistingLocalAccountIsAdoptedByNameButOtherProvidersAreNot()
    {
        var actions = Plan(
            Array.Empty<UserLink>(),
            new[]
            {
                new JellyfinAccount(A, "Alice", Default, false, false),
                new JellyfinAccount(B, "bob", Ldap, false, false),
            },
            U("1", "alice", admin: true),
            U("2", "bob"));
        Assert.Contains(new SyncAction.Link("1", A), actions);
        Assert.Contains(new SyncAction.SetPolicy(A, true, false), actions);
        Assert.DoesNotContain(actions, a => a is SyncAction.Link { KubarrUserId: "2" });
    }

    [Fact]
    public void AccountsAreNeverCreatedAndUnlinkedOnesAreLeftAlone()
    {
        var actions = Plan(Array.Empty<UserLink>(), new[] { new JellyfinAccount(C, "breakglass", Default, true, false) }, U("1", "alice"));
        Assert.Empty(actions);
    }

    [Fact]
    public void DeletedJellyfinAccountDropsItsLink()
    {
        var actions = Plan(new[] { L("1", A) }, Array.Empty<JellyfinAccount>(), U("1", "alice"));
        Assert.Equal(new SyncAction[] { new SyncAction.Unlink("1") }, actions);
    }

    [Fact]
    public void RenameIntoATakenNameIsSkipped()
    {
        var actions = Plan(
            new[] { L("1", A) },
            new[] { new JellyfinAccount(A, "alice", Kubarr, false, false), new JellyfinAccount(B, "carol", Default, false, false) },
            U("1", "carol"));
        Assert.DoesNotContain(actions, a => a is SyncAction.Rename);
    }
}
