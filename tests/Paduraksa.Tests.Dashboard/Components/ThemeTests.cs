using Bunit;

using Microsoft.Extensions.DependencyInjection;

using MudBlazor;

using Paduraksa.Dashboard.Layout;
using Paduraksa.Dashboard.Theme;

namespace Paduraksa.Tests.Dashboard.Components;

public sealed class ThemeTests
{
    [Theory]
    [InlineData(ThemePreference.System, false, true)]
    [InlineData(ThemePreference.System, true, false)]
    [InlineData(ThemePreference.Dark, true, true)]
    [InlineData(ThemePreference.Light, false, false)]
    public void ThePaletteIs_TheChoice_ElseTheSystems_ElseDark(ThemePreference preference, bool systemPrefersLight, bool dark)
    {
        ThemeState theme = new();
        theme.Initialize(preference, systemPrefersLight);

        Assert.Equal(dark, theme.IsDark);
    }

    [Fact]
    public void TheCircuitKeepsItsOwnState_AndASystemChangeIsAnnouncedOnce()
    {
        ThemeState theme = new();
        theme.Initialize(ThemePreference.System, systemPrefersLight: false);
        theme.Initialize(ThemePreference.Light, systemPrefersLight: true);
        int changes = 0;
        theme.Changed += () => changes++;

        theme.SetSystemPreference(prefersLight: false);
        theme.SetSystemPreference(prefersLight: true);

        Assert.Equal(ThemePreference.System, theme.Preference);
        Assert.Equal(1, changes);
        Assert.False(theme.IsDark);
    }

    [Theory]
    [InlineData("dark", ThemePreference.Dark)]
    [InlineData("light", ThemePreference.Light)]
    [InlineData("system", ThemePreference.System)]
    [InlineData(null, ThemePreference.System)]
    [InlineData("<b>", ThemePreference.System)]
    public void TheCookieNamesAPreference(string? cookie, ThemePreference preference) =>
        Assert.Equal(preference, ThemeState.ParsePreference(cookie));

    [Fact]
    public async Task TheToggle_FlipsThePalette_AndRemembersTheChoice()
    {
        await using DashboardTestContext context = new();
        context.JSInterop.SetupVoid("paduraksa.setTheme", _ => true);
        ThemeState theme = context.Services.GetRequiredService<ThemeState>();
        theme.Initialize(ThemePreference.System, systemPrefersLight: false);
        context.Render<MudPopoverProvider>();
        IRenderedComponent<ThemeToggle> toggle = context.Render<ThemeToggle>();

        Assert.Equal("Switch to the light theme", toggle.Find("button").GetAttribute("aria-label"));
        toggle.Find("button").Click();

        // The handler flips the state, then awaits the script; the re-render and the script call are both later than the
        // click. Wait for each of them rather than reading what may still be pending.
        toggle.WaitForAssertion(() => Assert.False(theme.IsDark));
        toggle.WaitForAssertion(() => Assert.Equal(ThemePreference.Light, theme.Preference));
        toggle.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations["paduraksa.setTheme"]));
        Assert.Equal("light", context.JSInterop.Invocations["paduraksa.setTheme"][^1].Arguments.Single());
        toggle.WaitForAssertion(() => Assert.Equal("Switch to the dark theme", toggle.Find("button").GetAttribute("aria-label")));

        toggle.Find("button").Click();

        toggle.WaitForAssertion(() => Assert.True(theme.IsDark));
        toggle.WaitForAssertion(() => Assert.Equal(ThemePreference.Dark, theme.Preference));
        toggle.WaitForAssertion(() => Assert.Equal(2, context.JSInterop.Invocations["paduraksa.setTheme"].Count));
        Assert.Equal("dark", context.JSInterop.Invocations["paduraksa.setTheme"][^1].Arguments.Single());
        toggle.WaitForAssertion(() => Assert.Equal("Switch to the light theme", toggle.Find("button").GetAttribute("aria-label")));
    }

    [Theory]
    [InlineData("Ada Lovelace", "AL")]
    [InlineData("owner", "O")]
    [InlineData("grace.hopper", "GH")]
    [InlineData("  ", "?")]
    public void TheUserMenuShowsInitials(string name, string initials) =>
        Assert.Equal(initials, UserMenu.Initials(name));
}
