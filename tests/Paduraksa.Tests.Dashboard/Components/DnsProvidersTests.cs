using Bunit;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

using MudBlazor;

using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Drawers;
using Paduraksa.Dashboard.Pages;
using Paduraksa.Dashboard.Providers;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// The DNS providers, listed as the certificates are: a table of rows that open a drawer (at its own address) with the certificates ordered
/// through the provider as links, a rename, Test, and Delete behind its confirmation and the permission.
/// </summary>
public sealed class DnsProvidersTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static DnsProviderSummary Provider(string label, DnsProviderKind kind = DnsProviderKind.Cloudflare, int ageDays = 3) =>
        new(DnsProviderId.New(), kind, label, Now.AddDays(-ageDays), Now.AddDays(-ageDays + 1));

    private static CertificateSummary Certificate(string name, DateTimeOffset notAfter) =>
        new(CertificateId.New(), CertificateSource.Acme, CertificateStatus.Active, CertificateValidity.Valid, [name], Now.AddDays(-30), notAfter, $"CN={name}",
            "CN=R11, O=Let's Encrypt", "01", "AA:BB", "EC P-256", Now.AddDays(-30), Now.AddDays(-30));

    private static (FakeProviders Providers, IRenderedComponent<DrawerHost> Drawers, IRenderedComponent<MudDialogProvider> Dialogs) Setup(DashboardTestContext context, Role role)
    {
        // Registered before the sign-in, which ends by building the services.
        FakeProviders providers = new();
        context.Services.AddSingleton<IDashboardProviders>(providers);
        ConfigPages.SignIn(context, role, new FixedTime(Now));
        var dialogs = context.Render<MudDialogProvider>();
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        return (providers, drawers, dialogs);
    }

    private static Task DrawerReady(IRenderedComponent<DrawerHost> drawers) =>
        drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=dns-provider-details] .pdk-detail").Count == 1);

    [Fact]
    public async Task TheList_IsATableOfTheProviders_WithTypeDatesAndHowManyCertificatesUseEach()
    {
        await using DashboardTestContext context = new();
        var (providers, _, _) = Setup(context, Role.Owner);
        var cloudflare = Provider("Cloudflare home");
        var desec = Provider("deSEC lab", DnsProviderKind.DeSec);
        providers.DnsProviders.AddRange([desec, cloudflare]);
        var one = Certificate("*.home.test", Now.AddDays(60));
        var two = Certificate("app.home.test", Now.AddDays(60));
        providers.Certificates.AddRange([one, two]);
        providers.Orders[one.Id] = (AcmeAccountId.New(), cloudflare.Id);
        providers.Orders[two.Id] = (AcmeAccountId.New(), cloudflare.Id);

        var page = context.Render<DnsProviders>();

        await page.WaitForStateAsync(() => page.FindAll("[data-provider]").Count == 2);
        Assert.Equal([cloudflare.Id.ToString(), desec.Id.ToString()], page.FindAll("[data-provider]").Select(r => r.GetAttribute("data-provider")));
        var row = page.Find($"[data-provider='{cloudflare.Id}']");
        Assert.Contains("Cloudflare home", row.TextContent, StringComparison.Ordinal);
        Assert.Equal("Cloudflare", row.QuerySelector("[data-label=Type]")!.TextContent);
        Assert.Equal("2", row.QuerySelector("[data-label=Certificates]")!.TextContent.Trim());
        Assert.Contains("None", page.Find($"[data-provider='{desec.Id}'] [data-label=Certificates]").TextContent, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("[data-field=filter]"));
    }

    [Fact]
    public async Task NoProviders_IsTheEmptyStateWithTheAddButton()
    {
        await using DashboardTestContext context = new();
        Setup(context, Role.Owner);

        var page = context.Render<DnsProviders>();

        await page.WaitForStateAsync(() => page.Markup.Contains("No DNS providers yet", StringComparison.Ordinal));
        Assert.Single(page.FindAll("[data-action=add-provider]"));
    }

    [Fact]
    public async Task ARowOpensItsAddress_AndTheAddressOpensTheDrawerWithTheDetailsAndTheCertificatesAsLinks()
    {
        await using DashboardTestContext context = new();
        var (providers, drawers, _) = Setup(context, Role.Owner);
        var provider = Provider("Cloudflare home");
        providers.DnsProviders.Add(provider);
        var wildcard = Certificate("*.home.test", Now.AddDays(10));
        var elsewhere = Certificate("elsewhere.test", Now.AddDays(80));
        providers.Certificates.AddRange([wildcard, elsewhere]);
        providers.Orders[wildcard.Id] = (AcmeAccountId.New(), provider.Id);
        providers.Orders[elsewhere.Id] = (AcmeAccountId.New(), null);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var page = context.Render<DnsProviders>();
        await page.WaitForStateAsync(() => page.FindAll("[data-provider]").Count == 1);

        await page.Find("[data-provider]").ClickAsync(new());

        Assert.EndsWith($"/certificates/dns/{provider.Id}", navigation.Uri, StringComparison.Ordinal);
        Assert.Equal($"/certificates/dns/{provider.Id}", DashboardRoutes.DnsProvider(provider.Id));

        page.Render(p => p.Add(x => x.Key, provider.Id.ToString()));
        await DrawerReady(drawers);
        var drawer = drawers.Find("[data-drawer=dns-provider-details]");
        Assert.Equal("Cloudflare home", drawer.QuerySelector("input[data-field=label]")!.GetAttribute("value"));
        Assert.Contains("Cloudflare", drawer.TextContent, StringComparison.Ordinal);
        Assert.Contains("api.cloudflare.com", drawer.TextContent, StringComparison.Ordinal);
        Assert.Contains("Token updated", drawer.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("vault", drawer.TextContent, StringComparison.OrdinalIgnoreCase);
        var link = Assert.Single(drawer.QuerySelectorAll(".pdk-cert-links li a"));
        Assert.Equal(("*.home.test", DashboardRoutes.Certificate(wildcard.Id)), (link.TextContent.Trim(), link.GetAttribute("href")));
        Assert.Contains("10 days", drawer.QuerySelector($"[data-certificate='{wildcard.Id}']")!.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("elsewhere.test", drawer.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAddressThatIsNotAProvider_OpensTheNotFoundDrawer_AndOneThatIsGone_SaysSo()
    {
        await using DashboardTestContext context = new();
        var (_, drawers, _) = Setup(context, Role.Owner);
        context.Render<DnsProviders>(p => p.Add(x => x.Key, AcmeAccountId.New().ToString()));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=not-found]").Count == 1);
        Assert.Contains("Provider not found", drawers.Find("[data-drawer=not-found]").TextContent, StringComparison.Ordinal);

        await using DashboardTestContext second = new();
        var (_, missing, _) = Setup(second, Role.Owner);
        second.Render<DnsProviders>(p => p.Add(x => x.Key, DnsProviderId.New().ToString()));
        await missing.WaitForStateAsync(() => missing.Markup.Contains("There is no such provider", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Renaming_SendsTheNewName_UpdatesTheTitleAndTheList_AndAnEditedNameMakesTheDrawerAskBeforeItCloses()
    {
        await using DashboardTestContext context = new();
        var (providers, drawers, _) = Setup(context, Role.Owner);
        var provider = Provider("Cloudflare home");
        providers.DnsProviders.Add(provider);
        var page = context.Render<DnsProviders>(p => p.Add(x => x.Key, provider.Id.ToString()));
        await DrawerReady(drawers);
        Assert.True(drawers.Find("[data-action=rename]").HasAttribute("disabled"));

        await drawers.Find("input[data-field=label]").InputAsync(new() { Value = "  Cloudflare (lab)  " });
        Assert.False(drawers.Find("[data-action=rename]").HasAttribute("disabled"));
        Assert.True(context.Services.GetRequiredService<DrawerStack>().AnyDirty);
        await drawers.Find("form.pdk-rename").SubmitAsync();

        Assert.Equal([$"rename {provider.Id} Cloudflare (lab)"], providers.Calls);
        var title = $"#{DrawerShell.TitleId(context.Services.GetRequiredService<DrawerStack>().Top!)}";
        await drawers.WaitForStateAsync(() => drawers.Find(title).TextContent.Contains("Cloudflare (lab)", StringComparison.Ordinal));
        Assert.False(context.Services.GetRequiredService<DrawerStack>().AnyDirty);
        Assert.True(drawers.Find("[data-action=rename]").HasAttribute("disabled"));
        await providers.ChangedAsync();
        await page.WaitForStateAsync(() => page.Find("[data-provider]").TextContent.Contains("Cloudflare (lab)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABlankName_IsRefusedByTheServiceAndTheReasonShownAtTheField()
    {
        await using DashboardTestContext context = new();
        var (providers, drawers, _) = Setup(context, Role.Owner);
        var provider = Provider("Cloudflare home");
        providers.DnsProviders.Add(provider);
        context.Render<DnsProviders>(p => p.Add(x => x.Key, provider.Id.ToString()));
        await DrawerReady(drawers);

        await drawers.Find("input[data-field=label]").InputAsync(new() { Value = "   " });
        await drawers.Find("form.pdk-rename").SubmitAsync();

        Assert.Contains("a DNS provider needs a label", drawers.Find("[data-drawer=dns-provider-details]").TextContent, StringComparison.Ordinal);
        Assert.Equal("Cloudflare home", providers.DnsProviders[0].Label);
    }

    [Fact]
    public async Task Test_RunsInTheDrawer_AndShowsTheVerdict()
    {
        await using DashboardTestContext context = new();
        var (providers, drawers, _) = Setup(context, Role.Owner);
        var provider = Provider("Cloudflare home");
        providers.DnsProviders.Add(provider);
        providers.TestResult = new DnsProviderTestResult(Found: true, Valid: false, "the token was revoked");
        context.Render<DnsProviders>(p => p.Add(x => x.Key, provider.Id.ToString()));
        await DrawerReady(drawers);

        await drawers.Find("[data-action=test]").ClickAsync(new());

        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-field=test] .pdk-test-result").Count == 1);
        Assert.Equal([$"test {provider.Id}"], providers.Calls);
        Assert.Contains("token was revoked", drawers.Find("[data-field=test]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_AsksFirst_DeletesAndClosesTheDrawer()
    {
        await using DashboardTestContext context = new();
        var (providers, drawers, dialogs) = Setup(context, Role.Owner);
        var provider = Provider("Cloudflare home");
        providers.DnsProviders.Add(provider);
        var stack = context.Services.GetRequiredService<DrawerStack>();
        context.Render<DnsProviders>(p => p.Add(x => x.Key, provider.Id.ToString()));
        await DrawerReady(drawers);

        // The click waits for the dialog's answer, so it isn't awaited here.
        var asking = drawers.Find("[data-action=delete]").ClickAsync(new());
        await dialogs.WaitForStateAsync(() => dialogs.FindAll(".pdk-confirm").Count == 1);
        Assert.Empty(providers.Calls);
        await dialogs.Find(".pdk-confirm").ClickAsync(new());
        await asking;

        Assert.Equal([$"delete {provider.Id}"], providers.Calls);
        await drawers.WaitForStateAsync(() => stack.Levels.Count == 0);
    }

    [Fact]
    public async Task WithoutProvidersWrite_TheDrawerOffersNoRenameTestOrDelete_AndTheListNoAddButton()
    {
        await using DashboardTestContext context = new();
        var (providers, drawers, _) = Setup(context, Role.Viewer);
        var provider = Provider("Cloudflare home");
        providers.DnsProviders.Add(provider);

        var page = context.Render<DnsProviders>(p => p.Add(x => x.Key, provider.Id.ToString()));

        await DrawerReady(drawers);
        Assert.Empty(drawers.FindAll("[data-action=rename], [data-action=test], [data-action=delete], [data-action=rotate]"));
        Assert.Empty(drawers.FindAll("input[data-field=label]"));
        Assert.Contains("Cloudflare home", drawers.Find("[data-drawer=dns-provider-details]").TextContent, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("[data-action=add-provider]"));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
