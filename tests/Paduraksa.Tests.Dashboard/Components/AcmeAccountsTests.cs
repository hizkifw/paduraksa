using Bunit;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

using MudBlazor;

using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Drawers;
using Paduraksa.Dashboard.Pages;
using Paduraksa.Dashboard.Providers;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// The ACME accounts, listed as the certificates are: Active and Deactivated, a table of rows that open a drawer (at its own address), the
/// certificates ordered with the account as links, and Deactivate in the drawer behind its confirmation and the permission.
/// </summary>
public sealed class AcmeAccountsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static AcmeAccountSummary Account(string directory, string? label = null, string? contact = "ops@example.com", AcmeAccountStatus status = AcmeAccountStatus.Active, string? eab = null, int ageDays = 1) =>
        new(AcmeAccountId.New(), directory, contact is null ? [] : [$"mailto:{contact}"], status, "https://acme.test/acct/7", JwsAlgorithm.ES256, true,
            Now.AddDays(-ageDays), Now.AddDays(-ageDays), eab, label);

    private static CertificateSummary Certificate(string name, DateTimeOffset notAfter, CertificateStatus status = CertificateStatus.Active) =>
        new(CertificateId.New(), CertificateSource.Acme, status, CertificateValidity.Valid, [name], Now.AddDays(-30), notAfter, $"CN={name}", "CN=R11, O=Let's Encrypt",
            "01", "AA:BB", "EC P-256", Now.AddDays(-30), Now.AddDays(-30));

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

    [Fact]
    public async Task TheList_ShowsTheActiveAccountsFirst_WithTheirCaKeyAndCertificateCount_AndTheOtherTabHasTheDeactivatedOnes()
    {
        await using DashboardTestContext context = new();
        var (providers, _, _) = Setup(context, Role.Owner);
        var staging = Account(ProviderDisplay.LetsEncryptStaging, eab: "kid-1", ageDays: 2);
        var production = Account(ProviderDisplay.LetsEncrypt, label: "Home", ageDays: 5);
        var old = Account("https://acme.zerossl.com/v2/DV90", status: AcmeAccountStatus.Deactivated, ageDays: 40);
        providers.Accounts.AddRange([production, staging, old]);
        var one = Certificate("app.home.test", Now.AddDays(60));
        providers.Certificates.Add(one);
        providers.Orders[one.Id] = (staging.Id, null);

        var page = context.Render<AcmeAccounts>();

        await page.WaitForStateAsync(() => page.FindAll("[data-account]").Count == 2);
        Assert.Equal([staging.Id.ToString(), production.Id.ToString()], page.FindAll("[data-account]").Select(r => r.GetAttribute("data-account")));
        var row = page.Find($"[data-account='{staging.Id}']");
        Assert.Contains("ops@example.com", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("Staging", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("EAB", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("ES256", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("None", page.Find($"[data-account='{production.Id}']").TextContent, StringComparison.Ordinal);
        Assert.Equal("1", page.Find($"[data-account='{staging.Id}'] [data-label=Certificates]").TextContent.Trim());
        Assert.Equal("true", page.Find("[data-field=filter] [data-value=active]").GetAttribute("aria-checked"));
        Assert.Equal("2", page.Find("[data-field=filter] [data-value=active] .pdk-seg-count").TextContent);

        await page.Find("[data-field=filter] [data-value=deactivated]").ClickAsync(new());

        Assert.Equal([old.Id.ToString()], page.FindAll("[data-account]").Select(r => r.GetAttribute("data-account")));
    }

    [Fact]
    public async Task AFilterWithNothingInIt_IsOneCompactLine_AndNoAccountsAtAllIsTheEmptyStateWithTheAddButton()
    {
        await using DashboardTestContext context = new();
        var (providers, _, _) = Setup(context, Role.Owner);
        providers.Accounts.Add(Account(ProviderDisplay.LetsEncrypt));
        var page = context.Render<AcmeAccounts>();
        await page.WaitForStateAsync(() => page.FindAll("[data-account]").Count == 1);

        await page.Find("[data-field=filter] [data-value=deactivated]").ClickAsync(new());

        Assert.Equal("No deactivated accounts.", page.Find("[data-empty=deactivated]").TextContent.Trim());
        Assert.Empty(page.FindAll(".pdk-dt"));

        await using DashboardTestContext bare = new();
        Setup(bare, Role.Owner);
        var empty = bare.Render<AcmeAccounts>();
        await empty.WaitForStateAsync(() => empty.FindAll(".pdk-empty").Count > 0 || empty.Markup.Contains("No ACME accounts yet", StringComparison.Ordinal));
        Assert.Single(empty.FindAll("[data-action=add-account]"));
    }

    [Fact]
    public async Task ARowOpensItsAddress_AndTheAddressOpensTheDrawerWithTheAccountsDetailsAndItsCertificatesAsLinks()
    {
        await using DashboardTestContext context = new();
        var (providers, drawers, _) = Setup(context, Role.Owner);
        var account = Account(ProviderDisplay.LetsEncrypt, label: "Home", eab: "kid-9");
        providers.Accounts.Add(account);
        var soon = Certificate("app.home.test", Now.AddDays(10));
        var later = Certificate("*.home.test", Now.AddDays(80));
        var other = Certificate("elsewhere.test", Now.AddDays(80));
        providers.Certificates.AddRange([soon, later, other]);
        providers.Orders[soon.Id] = (account.Id, null);
        providers.Orders[later.Id] = (account.Id, null);
        providers.Orders[other.Id] = (AcmeAccountId.New(), null);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var page = context.Render<AcmeAccounts>();
        await page.WaitForStateAsync(() => page.FindAll("[data-account]").Count == 1);

        await page.Find("[data-account]").ClickAsync(new());

        Assert.EndsWith(DashboardRoutes.AcmeAccount(account.Id), navigation.Uri, StringComparison.Ordinal);
        Assert.Equal($"/certificates/acme/{account.Id}", DashboardRoutes.AcmeAccount(account.Id));

        // The route's key opens the drawer (the page is told the address's last segment).
        page.Render(p => p.Add(x => x.Key, account.Id.ToString()));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=acme-account-details] .pdk-detail").Count == 1);
        var drawer = drawers.Find("[data-drawer=acme-account-details]");
        Assert.Contains("Home", drawer.TextContent, StringComparison.Ordinal);
        Assert.Equal("Active", drawer.QuerySelector("[data-field=status]")!.TextContent);
        Assert.Contains("Let's Encrypt", drawer.TextContent, StringComparison.Ordinal);
        Assert.Contains(ProviderDisplay.LetsEncrypt, drawer.TextContent, StringComparison.Ordinal);
        Assert.Contains("ops@example.com", drawer.TextContent, StringComparison.Ordinal);
        Assert.Contains("ES256", drawer.TextContent, StringComparison.Ordinal);
        Assert.Contains("kid-9", drawer.TextContent, StringComparison.Ordinal);
        Assert.Contains("https://acme.test/acct/7", drawer.TextContent, StringComparison.Ordinal);
        Assert.NotNull(drawer.QuerySelector("[data-action=copy-account-url]"));

        var links = drawer.QuerySelectorAll(".pdk-cert-links li a").ToDictionary(a => a.TextContent.Trim(), a => a.GetAttribute("href"));
        Assert.Equal(2, links.Count);
        Assert.Equal(DashboardRoutes.Certificate(soon.Id), links["app.home.test"]);
        Assert.Equal(DashboardRoutes.Certificate(later.Id), links["*.home.test"]);
        Assert.DoesNotContain("elsewhere.test", drawer.TextContent, StringComparison.Ordinal);
        Assert.Contains("Active", drawer.QuerySelector($"[data-certificate='{soon.Id}']")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("10 days", drawer.QuerySelector($"[data-certificate='{soon.Id}']")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAddressThatIsNotAnAccount_OpensTheNotFoundDrawer_AndOneThatIsGone_SaysSo()
    {
        await using DashboardTestContext context = new();
        var (_, drawers, _) = Setup(context, Role.Owner);

        context.Render<AcmeAccounts>(p => p.Add(x => x.Key, "not-an-id"));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=not-found]").Count == 1);
        Assert.Contains("Account not found", drawers.Find("[data-drawer=not-found]").TextContent, StringComparison.Ordinal);

        // A well-formed ID of another type is not an account either.
        await using DashboardTestContext second = new();
        var (_, other, _) = Setup(second, Role.Owner);
        second.Render<AcmeAccounts>(p => p.Add(x => x.Key, HostId.New().ToString()));
        await other.WaitForStateAsync(() => other.FindAll("[data-drawer=not-found]").Count == 1);

        await using DashboardTestContext third = new();
        var (_, missing, _) = Setup(third, Role.Owner);
        third.Render<AcmeAccounts>(p => p.Add(x => x.Key, AcmeAccountId.New().ToString()));
        await missing.WaitForStateAsync(() => missing.Markup.Contains("There is no such account", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deactivate_IsInTheDrawer_AsksFirst_AndMovesTheAccountToTheDeactivatedTab()
    {
        await using DashboardTestContext context = new();
        var (providers, drawers, dialogs) = Setup(context, Role.Owner);
        var account = Account(ProviderDisplay.LetsEncrypt, label: "Home");
        providers.Accounts.Add(account);
        var page = context.Render<AcmeAccounts>(p => p.Add(x => x.Key, account.Id.ToString()));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=acme-account-details] .pdk-detail").Count == 1);
        Assert.Empty(page.FindAll(".pdk-account-delete"));

        // The click waits for the dialog's answer, so it isn't awaited here.
        var asking = drawers.Find("[data-drawer=acme-account-details] [data-action=delete]").ClickAsync(new());
        await dialogs.WaitForStateAsync(() => dialogs.FindAll(".pdk-confirm").Count == 1);
        Assert.Contains("deactivated for good", dialogs.Markup, StringComparison.Ordinal);
        await dialogs.Find(".pdk-confirm").ClickAsync(new());
        await asking;

        Assert.Equal([$"deactivate {account.Id}"], providers.Calls);
        await drawers.WaitForStateAsync(() => drawers.Find("[data-field=status]").TextContent == "Deactivated");
        Assert.Empty(drawers.FindAll("[data-drawer=acme-account-details] [data-action=delete]"));

        // The change feed tells the list, which no longer shows it under Active.
        await providers.ChangedAsync();
        await page.WaitForStateAsync(() => page.FindAll("[data-account]").Count == 0);
        await page.Find("[data-field=filter] [data-value=deactivated]").ClickAsync(new());
        Assert.Single(page.FindAll($"[data-account='{account.Id}']"));
    }

    [Fact]
    public async Task Deactivate_Declined_ChangesNothing_AndARefusalLeavesTheAccountActive()
    {
        await using DashboardTestContext context = new();
        var (providers, drawers, dialogs) = Setup(context, Role.Owner);
        var account = Account(ProviderDisplay.LetsEncrypt);
        providers.Accounts.Add(account);
        providers.DeactivateStatus = IssuanceChangeStatus.Invalid;
        context.Render<AcmeAccounts>(p => p.Add(x => x.Key, account.Id.ToString()));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=acme-account-details] .pdk-detail").Count == 1);

        var declined = drawers.Find("[data-action=delete]").ClickAsync(new());
        await dialogs.WaitForStateAsync(() => dialogs.FindAll(".pdk-confirm").Count == 1);
        await dialogs.InvokeAsync(() => dialogs.FindAll(".mud-dialog-actions button").First(b => b.TextContent.Contains("Cancel", StringComparison.Ordinal)).Click());
        await declined;
        Assert.Empty(providers.Calls);

        var asking = drawers.Find("[data-action=delete]").ClickAsync(new());
        await dialogs.WaitForStateAsync(() => dialogs.FindAll(".pdk-confirm").Count == 1);
        await dialogs.Find(".pdk-confirm").ClickAsync(new());
        await asking;

        Assert.Equal([$"deactivate {account.Id}"], providers.Calls);
        Assert.Equal("Active", drawers.Find("[data-field=status]").TextContent);
    }

    [Fact]
    public async Task WithoutProvidersWrite_TheDrawerAndThePageOfferNoChange()
    {
        await using DashboardTestContext context = new();
        var (providers, drawers, _) = Setup(context, Role.Viewer);
        var account = Account(ProviderDisplay.LetsEncrypt);
        providers.Accounts.Add(account);

        var page = context.Render<AcmeAccounts>(p => p.Add(x => x.Key, account.Id.ToString()));

        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=acme-account-details] .pdk-detail").Count == 1);
        Assert.Empty(drawers.FindAll("[data-action=delete]"));
        Assert.Empty(page.FindAll("[data-action=add-account]"));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
