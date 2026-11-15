using Bunit;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

using MudBlazor;

using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Notifications;
using Paduraksa.Contracts.Revisions;
using Paduraksa.Contracts.Simulation;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Audit;
using Paduraksa.Dashboard.Components;
using Paduraksa.Dashboard.Config;
using Paduraksa.Dashboard.Drawers;
using Paduraksa.Dashboard.LogFiles;
using Paduraksa.Dashboard.Pages;
using Paduraksa.Dashboard.SettingsArea;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// A reference to another configured resource is a link to its page or drawer: in the hosts list, the pickers, the audit log, the history, the live
/// tail and the simulator. One that points at something that is gone stays plain text.
/// </summary>
public sealed class ReferenceLinksTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly UpstreamConfig App = new(UpstreamId.New(), "app", Version: 1, Destinations: [new DestinationConfig(DestinationId.New(), "http://app:8080")]);

    private static CertificateSummary Certificate() =>
        new(CertificateId.New(), CertificateSource.Uploaded, CertificateStatus.Active, CertificateValidity.Valid, ["app.example.com"], Now.AddDays(-30), Now.AddDays(60),
            "CN=app.example.com", "CN=Test CA", "01", "AA:BB", "EC P-256", Now.AddDays(-30), Now.AddDays(-30));

    [Fact]
    public async Task TheHostsList_LinksTheUpstreamAHostForwardsToAndTheCertificateServingIt()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator, new FixedTime(Now));
        var certificate = Certificate();
        HostConfig bound = new(HostId.New(), ["app.example.com"], Version: 2, Routes: [new RouteConfig(RouteId.New(), App.Id)],
            Certificate: new CertificateBinding { Mode = CertificateBindingMode.Specific, CertificateId = certificate.Id });
        HostConfig redirect = new(HostId.New(), ["old.example.com"], Kind: HostKind.Redirect, Redirect: new RedirectConfig("https://app.example.com"));
        config.Document = new ConfigDocument(Upstreams: [App], Hosts: [bound, redirect]);
        config.Certificates = [certificate];

        var page = context.Render<Hosts>();

        await page.WaitForStateAsync(() => page.FindAll(".pdk-hosts-table").Count == 1);
        var row = page.Find("[data-host='app.example.com']");
        Assert.Equal(DashboardRoutes.Upstream(App.Id), row.QuerySelector("a[data-link=upstream]")!.GetAttribute("href"));
        Assert.Equal(DashboardRoutes.Certificate(certificate.Id), row.QuerySelector("a[data-link=certificate]")!.GetAttribute("href"));
        var other = page.Find("[data-host='old.example.com']");
        Assert.Empty(other.QuerySelectorAll("a[data-link=upstream]"));
    }

    [Fact]
    public void AnUpstreamThatIsNotInTheConfig_IsNotLinkedFromTheHostsDestinations()
    {
        HostConfig host = new(HostId.New(), ["app.test"], Routes: [new RouteConfig(RouteId.New(), UpstreamId.New())]);

        var destination = Assert.Single(HostOverview.DestinationsOf(host, new ConfigDocument(Hosts: [host])));

        Assert.True(destination.Missing);
        Assert.Null(destination.UpstreamId);
    }

    [Fact]
    public async Task ARateLimitPickerWithAPolicyChosen_HasAnOpenLinkToIt_AndOneWithoutOrWithAStagedPolicyHasNone()
    {
        await using DashboardTestContext context = new();
        RateLimitPolicyConfig strict = new(RateLimitPolicyId.New(), "Strict API", Requests: 10, Per: RateLimitPeriod.Second);

        var chosen = context.Render<RateLimitField>(p => p
            .Add(f => f.Value, new RateLimitSettings { Enabled = true, PolicyId = strict.Id })
            .Add(f => f.Inherited, new RateLimitSettings())
            .Add(f => f.Policies, [strict]));
        var inherit = context.Render<RateLimitField>(p => p.Add(f => f.Inherited, new RateLimitSettings()).Add(f => f.Policies, [strict]));
        var staged = context.Render<RateLimitField>(p => p
            .Add(f => f.Value, new RateLimitSettings { Enabled = true, PolicyId = strict.Id })
            .Add(f => f.Inherited, new RateLimitSettings())
            .Add(f => f.Policies, [strict])
            .Add(f => f.Staged, new HashSet<RateLimitPolicyId> { strict.Id }));

        var open = chosen.Find("a[data-action=open-ref]");
        Assert.Equal(DashboardRoutes.RateLimit(strict.Id), open.GetAttribute("href"));
        Assert.Equal("Open Strict API", open.GetAttribute("aria-label"));
        Assert.Empty(inherit.FindAll("[data-action=open-ref]"));
        Assert.Empty(staged.FindAll("[data-action=open-ref]"));
    }

    [Fact]
    public async Task TheAccessListAuthProviderAndRuleSetPickers_LinkTheirChoice_ABuiltInPresetToTheRulesList()
    {
        await using DashboardTestContext context = new();
        AccessListConfig list = new(AccessListId.New(), "LAN only");
        AuthProviderConfig provider = new(AuthProviderId.New(), "Authelia");
        RuleSetConfig mine = new(RuleSetId.New(), "Mine");
        var preset = RuleSetPresets.All[0];

        var accessList = context.Render<AccessListField>(p => p
            .Add(f => f.Value, new AccessListSettings { Enabled = true, AccessListId = list.Id }).Add(f => f.Inherited, new AccessListSettings()).Add(f => f.Lists, [list]));
        var auth = context.Render<AuthProviderField>(p => p
            .Add(f => f.Value, new AuthProviderSettings { Enabled = true, AuthProviderId = provider.Id }).Add(f => f.Inherited, new AuthProviderSettings()).Add(f => f.Providers, [provider]));
        var rules = context.Render<RuleSetField>(p => p
            .Add(f => f.Value, new RuleSetSettings { Enabled = true, RuleSetId = mine.Id }).Add(f => f.Inherited, new RuleSetSettings()).Add(f => f.Sets, [mine]));
        var presetRules = context.Render<RuleSetField>(p => p
            .Add(f => f.Value, new RuleSetSettings { Enabled = true, RuleSetId = preset.Id }).Add(f => f.Inherited, new RuleSetSettings()).Add(f => f.Sets, [mine]));

        Assert.Equal(DashboardRoutes.AccessList(list.Id), accessList.Find("a[data-action=open-ref]").GetAttribute("href"));
        Assert.Equal(DashboardRoutes.AuthProvider(provider.Id), auth.Find("a[data-action=open-ref]").GetAttribute("href"));
        Assert.Equal(DashboardRoutes.RuleSet(mine.Id), rules.Find("a[data-action=open-ref]").GetAttribute("href"));
        Assert.Equal(DashboardRoutes.RuleSets, presetRules.Find("a[data-action=open-ref]").GetAttribute("href"));
    }

    [Fact]
    public async Task TheAuditLog_LinksATargetThatStillExists_AndLeavesADeletedOneAsText()
    {
        var host = HostId.New();
        var gone = UpstreamId.New();
        var revision = ConfigRevisionId.New();
        var certificate = CertificateId.New();
        var account = AcmeAccountId.New();
        var dns = DnsProviderId.New();
        var limit = RateLimitPolicyId.New();
        HashSet<string> existing = [host.ToString(), certificate.ToString(), account.ToString(), dns.ToString(), limit.ToString()];

        Assert.Equal(DashboardRoutes.Host(host), AuditText.LinkOf(host.ToString(), existing));
        Assert.Equal(DashboardRoutes.Certificate(certificate), AuditText.LinkOf(certificate.ToString(), existing));
        Assert.Equal(DashboardRoutes.AcmeAccount(account), AuditText.LinkOf(account.ToString(), existing));
        Assert.Equal(DashboardRoutes.DnsProvider(dns), AuditText.LinkOf(dns.ToString(), existing));
        Assert.Equal(DashboardRoutes.RateLimit(limit), AuditText.LinkOf(limit.ToString(), existing));
        Assert.Null(AuditText.LinkOf(gone.ToString(), existing));
        Assert.Null(AuditText.LinkOf(host.ToString(), null));
        Assert.Equal(DashboardRoutes.Revision(revision), AuditText.LinkOf(revision.ToString(), existing));
        Assert.Null(AuditText.LinkOf(null, existing));
        await Task.CompletedTask;
    }

    [Fact]
    public void AnAuditEventsUser_IsLinkedToTheirDrawer_YouToMyAccount_AndATokenOrTheCliToNothing()
    {
        var other = UserId.New();
        var me = UserId.New();
        Dictionary<UserId, string> users = new() { [other] = "Grace", [me] = "Ada" };

        Assert.Equal(SettingsRoutes.User(other), AuditText.ActorHref(new ActorRef(ActorKind.User, other), me, users));
        Assert.Equal(DashboardRoutes.Account, AuditText.ActorHref(new ActorRef(ActorKind.User, me), me, users));
        Assert.Null(AuditText.ActorHref(new ActorRef(ActorKind.User, UserId.New()), me, users));
        Assert.Null(AuditText.ActorHref(new ActorRef(ActorKind.Cli), me, users));
        Assert.Null(AuditText.ActorHref(new ActorRef(ActorKind.Token, Token: ApiTokenId.New()), me, users));
    }

    [Fact]
    public async Task APastRevisionsDiff_LinksTheObjectsThatStillExistAndNotTheOnesThatWereDeleted()
    {
        await using DashboardTestContext context = new();
        var kept = HostId.New();
        var deleted = HostId.New();
        HashSet<string> existing = [kept.ToString()];
        ObjectChange Change(HostId id) => new(ConfigChangeKind.Modified, ChangedObject.Host, "app.example.com", [], id.ToString());
        ChangeGroup Group(HostId id) => new(Change(id), []);

        var list = context.Render<ChangeList>(p => p
            .Add(c => c.Groups, [Group(kept), Group(deleted)])
            .Add(c => c.LinkOf, change => AuditText.LinkOf(change.Key, existing)));
        var review = context.Render<ChangeList>(p => p.Add(c => c.Groups, [Group(kept)]));

        Assert.Equal(DashboardRoutes.Host(kept), Assert.Single(list.FindAll("a[data-link=object]")).GetAttribute("href"));
        Assert.Empty(review.FindAll("a[data-link=object]"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ADeliverysChannel_IsLinkedToItsDrawer_AndPlainWithoutAnHref()
    {
        await using DashboardTestContext context = new();
        var channel = NotificationChannelId.New();
        NotificationDeliverySummary delivery = new(NotificationDeliveryId.New(), channel, NotificationDeliveryState.Sent, 1, ChannelLabel: "Ops hook");

        var linked = context.Render<Paduraksa.Dashboard.NotificationsArea.DeliveryChip>(p => p
            .Add(c => c.Delivery, delivery).Add(c => c.Href, SettingsRoutes.NotificationChannel(channel)));
        var plain = context.Render<Paduraksa.Dashboard.NotificationsArea.DeliveryChip>(p => p.Add(c => c.Delivery, delivery));

        Assert.Equal($"/settings/notifications/{channel}", linked.Find("a[data-link=channel]").GetAttribute("href"));
        Assert.Empty(plain.FindAll("a"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ALiveTailRow_LinksTheHostThatAnsweredWhenItIsKnown()
    {
        await using DashboardTestContext context = new();
        var host = HostId.New();
        Paduraksa.Contracts.Logging.LogTailEntry entry = new(1, Now, 12.5, Host: "app.example.com", MatchedHost: "app.example.com", Method: "GET", Path: "/", Status: 200);

        var linked = context.Render<LiveTailRow>(p => p.Add(r => r.Entry, entry).Add(r => r.HostHref, DashboardRoutes.Host(host)));
        var plain = context.Render<LiveTailRow>(p => p.Add(r => r.Entry, entry));

        Assert.Equal(DashboardRoutes.Host(host), linked.Find(".pdk-tail-host a[data-link=host]").GetAttribute("href"));
        Assert.Empty(plain.FindAll(".pdk-tail-host a"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ASimulationAgainstTheLiveConfig_LinksTheUpstreamAndTheMatchedHost_ButNotInAReview()
    {
        await using DashboardTestContext context = new();
        var host = HostId.New();
        SimulationResult result = new(
            SimulationStatus.Simulated,
            new SimulationSource(SimulationTargetKind.Live),
            new SimulationOutcome(SimulationOutcomeKind.ForwardToUpstream, "Forwards to app"),
            [new SimulationStep(SimulationStepKind.HostMatch, SimulationStepVerdict.Passed, "Matched app.example.com", [], Host: host)],
            new SimulationUpstream(App.Id, [new SimulationDestination(DestinationId.New(), "http://app:8080", SimulationHealth.Healthy)]));

        var live = context.Render<SimulationView>(p => p.Add(v => v.Result, result).Add(v => v.UpstreamName, "app").Add(v => v.LinkObjects, true));
        var review = context.Render<SimulationView>(p => p.Add(v => v.Result, result).Add(v => v.UpstreamName, "app").Add(v => v.Compact, true));

        Assert.Equal(DashboardRoutes.Upstream(App.Id), live.Find("a[data-link=upstream]").GetAttribute("href"));
        Assert.Equal(DashboardRoutes.Host(host), live.Find("a[data-link=host]").GetAttribute("href"));
        Assert.Empty(review.FindAll("a[data-link]"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AUsersAddress_OpensTheirDrawerForAWriter_NotFoundForAnId_AndTheListAloneForAReader()
    {
        await using DashboardTestContext context = new();
        var (_, _, _, me) = IdentityPages.SignIn(context, Role.Owner);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();

        var page = context.Render<Users>(p => p.Add(x => x.Key, me.UserId.ToString()));

        await drawers.WaitForStateAsync(() => drawers.FindAll("form[data-drawer=user] [data-readonly=username]").Count == 1);
        Assert.Equal("ada", drawers.Find("[data-readonly=username] [data-part=value]").TextContent);
        Assert.Equal(SettingsRoutes.User(me.UserId), page.Find("a[data-link=user]").GetAttribute("href"));

        await using DashboardTestContext second = new();
        IdentityPages.SignIn(second, Role.Owner);
        second.Render<MudPopoverProvider>();
        var missing = second.Render<DrawerHost>();
        second.Render<Users>(p => p.Add(x => x.Key, UserId.New().ToString()));
        await missing.WaitForStateAsync(() => missing.FindAll("[data-drawer=not-found]").Count == 1);

        await using DashboardTestContext reader = new();
        var (_, _, _, viewer) = IdentityPages.SignIn(reader, Role.Admin);
        reader.Render<MudPopoverProvider>();
        var none = reader.Render<DrawerHost>();
        var readOnly = reader.Render<Users>(p => p.Add(x => x.Key, viewer.UserId.ToString()));
        await readOnly.WaitForStateAsync(() => readOnly.FindAll("[data-user]").Count == 1);
        Assert.Empty(none.FindAll(".pdk-sheet"));
        Assert.Empty(readOnly.FindAll("a[data-link=user]"));
    }

    [Fact]
    public async Task ADrawerOpenedByAnotherPageBeforeThisOneGoes_StaysOpen_WhileThisPagesOwnLevelsClose()
    {
        // A link from a drawer to a drawer on another page: the new page pushes its level (its parameters are set first), and only then is the old
        // page disposed. The old page's levels close; the new page's drawer, above them in the stack, must not.
        await using DashboardTestContext context = new();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        DrawerStack stack = new();
        RoutedDrawer certificates = new(navigation, DashboardRoutes.Certificates);
        certificates.Show("crt", _ => stack.Push<NotFoundDrawer>(NotFoundDrawer.For("Certificate", "c")));
        var stackedOnIt = stack.Push<NotFoundDrawer>(NotFoundDrawer.For("Stacked", "s"));
        RoutedDrawer hosts = new(navigation, DashboardRoutes.Hosts);
        hosts.Show("host", _ => stack.Push<NotFoundDrawer>(NotFoundDrawer.For("Host", "h")));

        certificates.Dispose();

        Assert.Same(hosts.Level, Assert.Single(stack.Levels));
        Assert.True(hosts.Level!.IsOpen);
        Assert.False(stackedOnIt.IsOpen);
        Assert.True((await stackedOnIt.Result).Canceled);
        hosts.Dispose();
        Assert.Empty(stack.Levels);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
