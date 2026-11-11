using Bunit;

using MudBlazor;

using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Security;
using Paduraksa.Contracts.Simulation;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Config;
using Paduraksa.Dashboard.Drawers;
using Paduraksa.Dashboard.Pages;
using Paduraksa.Dashboard.SettingsArea;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>The countries of an access list: two searchable pickers (Tor and unknown first, the rest by name), chips for what is held,
/// a hint while the proxy mode can't say where a request came from, and the countries in the list page's rule summary.</summary>
public sealed class AccessListCountriesTests
{
    private const string Drawer = "[data-drawer=access-list]";

    private static readonly AccessListConfig Geo = new(
        AccessListId.New(),
        "Europe",
        Version: 2,
        Ip: new AccessListIp(Countries: new AccessListCountries(["DE", "NL"], ["RU", "T1", "XX"])));

    private static ConfigDocument Document(TrustedProxyMode mode, params AccessListConfig[] lists) =>
        new(Settings: new GlobalSettings(TrustedProxies: new TrustedProxySettings(mode, mode == TrustedProxyMode.CloudflareTunnel ? ["127.0.0.1"] : [])), AccessLists: lists);

    private static async Task<(DashboardTestContext Context, FakeDashboardConfig Config, IRenderedComponent<DrawerHost> Drawers)> OpenAsync(
        ConfigDocument document, string key, Role role = Role.Operator)
    {
        DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, role);
        config.Document = document;
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<AccessLists>(parameters => parameters.Add(p => p.Key, key));
        await drawers.WaitForStateAsync(() => drawers.FindAll($"{Drawer} [data-section=countries]").Count == 1);
        return (context, config, drawers);
    }

    private static MudAutocomplete<string> Picker(IRenderedComponent<DrawerHost> drawers, string label) =>
        drawers.FindComponents<MudAutocomplete<string>>().Single(picker => picker.Instance.Label == label).Instance;

    private static async Task<List<string>> SearchAsync(IRenderedComponent<DrawerHost> drawers, string label, string text)
    {
        var found = await Picker(drawers, label).SearchFunc!(text, CancellationToken.None)!;
        return [.. found ?? []];
    }

    private static async Task PickAsync(IRenderedComponent<DrawerHost> drawers, string label, string code) =>
        await drawers.InvokeAsync(() => Picker(drawers, label).ValueChanged.InvokeAsync(code));

    private static string[] Chips(IRenderedComponent<DrawerHost> drawers, string field) =>
        [.. drawers.FindAll($"{Drawer} [data-field={field}] [data-country]").Select(chip => chip.GetAttribute("data-country")!)];

    [Fact]
    public async Task TheSectionSaysTheCountryComesFromCloudflare()
    {
        var (context, _, drawers) = await OpenAsync(Document(TrustedProxyMode.CloudflareProxy), DashboardRoutes.NewSegment);
        await using (context)
        {
            var section = drawers.Find($"{Drawer} [data-section=countries]").TextContent;

            Assert.Contains("as Cloudflare reports it", section, StringComparison.Ordinal);
            Assert.Contains("unless Unknown country is allowed", section, StringComparison.Ordinal);
            Assert.Empty(drawers.FindAll("[data-banner=no-country-source]"));
        }
    }

    [Theory]
    [InlineData(TrustedProxyMode.Off, true)]
    [InlineData(TrustedProxyMode.Generic, true)]
    [InlineData(TrustedProxyMode.CloudflareProxy, false)]
    [InlineData(TrustedProxyMode.CloudflareTunnel, false)]
    public async Task AHintShowsWhileTheProxyModeIsntCloudflare(TrustedProxyMode mode, bool hint)
    {
        var (context, _, drawers) = await OpenAsync(Document(mode), DashboardRoutes.NewSegment);
        await using (context)
        {
            var banners = drawers.FindAll("[data-banner=no-country-source]");

            Assert.Equal(hint ? 1 : 0, banners.Count);
            if (hint)
            {
                Assert.Contains("every visitor's country is unknown", banners[0].TextContent, StringComparison.Ordinal);
                Assert.Equal(DashboardRoutes.Settings, banners[0].QuerySelector("a")!.GetAttribute("href"));
            }
        }
    }

    [Fact]
    public async Task TheSatisfyHelpSaysWhatAPasswordDoesForACountry()
    {
        var withUsers = Geo with { BasicAuth = new AccessListBasicAuth(Users: [new AccessListUserConfig("alice", PasswordCredentialId.New())]), Satisfy = AccessListSatisfy.Any };
        var (context, _, drawers) = await OpenAsync(Document(TrustedProxyMode.CloudflareProxy, withUsers), withUsers.Id.ToString());
        await using (context)
        {
            var text = drawers.Find(Drawer).TextContent;

            Assert.Contains("a blocked country included, can still sign in", text, StringComparison.Ordinal);
            Assert.Contains("A denied address or country is refused even with the right password", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheSearchPutsTorAndUnknownFirstThenCountriesByNameWithTheirCode()
    {
        var (context, _, drawers) = await OpenAsync(Document(TrustedProxyMode.CloudflareProxy), DashboardRoutes.NewSegment);
        await using (context)
        {
            var all = await SearchAsync(drawers, "Allowed countries", string.Empty);

            Assert.Equal(["T1", "XX"], all.Take(2));
            Assert.Equal("Tor exit nodes", CountryPicker.Display("T1"));
            Assert.Equal("Unknown country", CountryPicker.Display("XX"));
            Assert.Equal("Germany (DE)", CountryPicker.Display("DE"));
            Assert.Equal(252, all.Count);
            var names = all.Skip(2).Select(CountryPicker.Display).ToList();
            Assert.Equal(names.Order(StringComparer.Ordinal), names);
        }
    }

    [Theory]
    [InlineData("germ", new[] { "DE" })]
    [InlineData("GERMANY", new[] { "DE" })]
    [InlineData("(de)", new[] { "DE" })]
    [InlineData("tor", new[] { "T1" })]
    [InlineData("unknown", new[] { "XX" })]
    [InlineData("zzzz", new string[0])]
    public async Task TheSearchMatchesNamesAndCodes(string text, string[] expected)
    {
        var (context, _, drawers) = await OpenAsync(Document(TrustedProxyMode.CloudflareProxy), DashboardRoutes.NewSegment);
        await using (context)
        {
            var found = await SearchAsync(drawers, "Allowed countries", text);

            Assert.Equal(expected, found.Where(code => expected.Contains(code)).ToArray());
            Assert.True(expected.Length == 0 ? found.Count == 0 : found.Count >= expected.Length);
        }
    }

    [Fact]
    public async Task AHeldCountryAndOneOnTheOtherSideAreNotOffered()
    {
        var (context, _, drawers) = await OpenAsync(Document(TrustedProxyMode.CloudflareProxy, Geo), Geo.Id.ToString());
        await using (context)
        {
            var allow = await SearchAsync(drawers, "Allowed countries", string.Empty);
            var deny = await SearchAsync(drawers, "Denied countries", string.Empty);

            Assert.DoesNotContain(allow, code => code is "DE" or "NL" or "RU" or "T1" or "XX");
            Assert.DoesNotContain(deny, code => code is "DE" or "NL" or "RU" or "T1" or "XX");
            Assert.Contains("FR", allow);
            Assert.Contains("FR", deny);
        }
    }

    [Fact]
    public async Task ThePickersShowWhatIsHeldAsChipsInTheOrderItWasPicked()
    {
        var (context, _, drawers) = await OpenAsync(Document(TrustedProxyMode.CloudflareProxy), DashboardRoutes.NewSegment);
        await using (context)
        {
            await PickAsync(drawers, "Allowed countries", "NL");
            await PickAsync(drawers, "Allowed countries", "DE");
            await PickAsync(drawers, "Denied countries", "T1");

            Assert.Equal(["NL", "DE"], Chips(drawers, "allowed-countries"));
            Assert.Equal(["T1"], Chips(drawers, "denied-countries"));
            Assert.Equal("Netherlands (NL)", drawers.Find($"{Drawer} [data-country=NL]").TextContent.Trim());
            Assert.Equal("Tor exit nodes", drawers.Find($"{Drawer} [data-country=T1]").TextContent.Trim());
        }
    }

    [Fact]
    public async Task APickedCountryIsntAddedTwice()
    {
        var (context, _, drawers) = await OpenAsync(Document(TrustedProxyMode.CloudflareProxy), DashboardRoutes.NewSegment);
        await using (context)
        {
            await PickAsync(drawers, "Allowed countries", "DE");
            await PickAsync(drawers, "Allowed countries", "de");

            Assert.Equal(["DE"], Chips(drawers, "allowed-countries"));
        }
    }

    [Fact]
    public async Task ARemovedChipGoesBackToTheSearch()
    {
        var (context, _, drawers) = await OpenAsync(Document(TrustedProxyMode.CloudflareProxy, Geo), Geo.Id.ToString());
        await using (context)
        {
            await drawers.InvokeAsync(() => drawers.FindComponents<MudChip<string>>().Single(chip => chip.Markup.Contains("data-country=\"DE\"", StringComparison.Ordinal)).Instance.OnClose.InvokeAsync(null!));

            Assert.Equal(["NL"], Chips(drawers, "allowed-countries"));
            Assert.Contains("DE", await SearchAsync(drawers, "Allowed countries", "germany"));
        }
    }

    [Fact]
    public async Task TheCountriesAreSentThroughReviewAsCodes()
    {
        var (context, config, drawers) = await OpenAsync(Document(TrustedProxyMode.CloudflareProxy), DashboardRoutes.NewSegment);
        await using (context)
        {
            await drawers.Find($"{Drawer} input[data-field=name]").InputAsync(new() { Value = "Europe" });
            await drawers.Find($"{Drawer} input[data-field=name]").BlurAsync(new());
            await PickAsync(drawers, "Allowed countries", "DE");
            await PickAsync(drawers, "Allowed countries", "XX");
            await PickAsync(drawers, "Denied countries", "T1");

            _ = drawers.Find($"form{Drawer}").SubmitAsync();
            await drawers.WaitForStateAsync(() => drawers.FindAll("[data-review=apply]").Count == 1);

            var list = Assert.IsType<UpsertAccessListCommand>(config.Executed[0]).AccessList;
            Assert.Equal(["DE", "XX"], list.Ip!.Countries!.Allow);
            Assert.Equal(["T1"], list.Ip.Countries.Deny);
            Assert.Empty(list.Ip.Allow);
        }
    }

    [Fact]
    public async Task AListWithNoCountriesSendsNone()
    {
        var plain = new AccessListConfig(AccessListId.New(), "Plain", Version: 1, Ip: new AccessListIp(["10.0.0.0/8"]));
        var (context, config, drawers) = await OpenAsync(Document(TrustedProxyMode.CloudflareProxy, plain), plain.Id.ToString());
        await using (context)
        {
            await drawers.Find($"{Drawer} input[data-field=name]").InputAsync(new() { Value = "Plain 2" });
            await drawers.Find($"{Drawer} input[data-field=name]").BlurAsync(new());
            _ = drawers.Find($"form{Drawer}").SubmitAsync();
            await drawers.WaitForStateAsync(() => drawers.FindAll("[data-review=apply]").Count == 1);

            Assert.Null(Assert.IsType<UpsertAccessListCommand>(config.Executed[0]).AccessList.Ip!.Countries);
        }
    }

    [Fact]
    public async Task AViewerSeesTheCountriesButCantChangeThem()
    {
        var (context, _, drawers) = await OpenAsync(Document(TrustedProxyMode.CloudflareProxy, Geo), Geo.Id.ToString(), Role.Viewer);
        await using (context)
        {
            Assert.Equal(["DE", "NL"], Chips(drawers, "allowed-countries"));
            Assert.Empty(drawers.FindAll($"{Drawer} [data-country] .mud-chip-close-button"));
            Assert.All(drawers.FindAll($"{Drawer} input[data-input]"), input => Assert.True(input.HasAttribute("readonly") || input.HasAttribute("disabled")));
        }
    }

    [Theory]
    [InlineData(1, 0, "only 1 country")]
    [InlineData(2, 0, "only 2 countries")]
    [InlineData(0, 3, "blocks 3 countries")]
    [InlineData(0, 1, "blocks 1 country")]
    [InlineData(2, 1, "only 2 countries, blocks 1 country")]
    public void TheListSummaryCountsCountries(int allowed, int denied, string expected)
    {
        string[] allow = ["DE", "NL", "FR"];
        string[] deny = ["RU", "CN", "KP"];
        AccessListConfig list = new(AccessListId.New(), "geo", Ip: new AccessListIp(Countries: new AccessListCountries([.. allow.Take(allowed)], [.. deny.Take(denied)])));

        Assert.Equal(expected, AccessListText.Summary(list));
    }

    [Fact]
    public void TheSummaryPutsCountriesBetweenAddressesAndUsersAndKeepsTheCombineMode()
    {
        AccessListConfig list = new(
            AccessListId.New(),
            "mixed",
            Ip: new AccessListIp(["10.0.0.0/8"], ["10.9.0.0/16"], Countries: new AccessListCountries(["DE", "NL"], ["RU", "CN", "KP"])),
            BasicAuth: new AccessListBasicAuth(Users: [new AccessListUserConfig("alice", PasswordCredentialId.New())]),
            Satisfy: AccessListSatisfy.Any);

        Assert.Equal("1 allowed, 1 denied, only 2 countries, blocks 3 countries, 1 user, address or password", AccessListText.Summary(list));
        Assert.Equal("only 1 country, 1 user, address and password", AccessListText.Summary(new AccessListConfig(AccessListId.New(), "c", Ip: new AccessListIp(Countries: new AccessListCountries(["DE"], [])), BasicAuth: list.BasicAuth)));
    }

    [Fact]
    public void ACountryOnlyListIsNotNoRules()
    {
        Assert.Equal("no rules", AccessListText.Summary(new AccessListConfig(AccessListId.New(), "none")));
        Assert.Equal("blocks 1 country", AccessListText.Summary(new AccessListConfig(AccessListId.New(), "geo", Ip: new AccessListIp(Countries: new AccessListCountries([], ["RU"])))));
    }

    [Theory]
    [InlineData(0, 0, 0, "no rules")]
    [InlineData(0, 0, 5, "countries only")]
    [InlineData(0, 4, 0, "addresses only")]
    [InlineData(0, 4, 2, "addresses and countries only")]
    [InlineData(2, 4, 2, "2 users")]
    public void TheSecurityOverviewSaysWhatAListLooksAt(int users, int ranges, int countries, string expected)
    {
        Assert.Equal(expected, SecurityFigures.ListText(new AccessListRef(AccessListId.New(), "list", users, ranges, countries)));
    }

    [Fact]
    public void TheSimulatorFormSendsACountryOnlyThroughCloudflare()
    {
        SimulationForm form = new() { Url = "https://app.test/", Country = " de ", Arrival = SimulationArrival.CloudflareProxy };

        Assert.Equal("de", form.ToRequest().Country);
        form.Arrival = SimulationArrival.CloudflareTunnel;
        Assert.Equal("de", form.ToRequest().Country);
        form.Arrival = SimulationArrival.TrustedProxy;
        Assert.Null(form.ToRequest().Country);
        form.Arrival = SimulationArrival.Direct;
        Assert.Null(form.ToRequest().Country);
        form.Arrival = SimulationArrival.CloudflareProxy;
        form.Country = "  ";
        Assert.Null(form.ToRequest().Country);
    }
}
