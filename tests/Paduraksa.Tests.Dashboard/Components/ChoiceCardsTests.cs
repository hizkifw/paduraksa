using Bunit;

using Microsoft.AspNetCore.Components.Web;

using Paduraksa.Dashboard.Components;

namespace Paduraksa.Tests.Dashboard.Components;

public sealed class ChoiceCardsTests
{
    public enum Mode
    {
        Merge,
        Replace,
    }

    private static readonly IReadOnlyList<ChoiceOption<Mode>> Options =
    [
        new(Mode.Merge, "Merge", "Adds what the file names."),
        new(Mode.Replace, "Replace", Key: "swap", Icon: MudBlazor.Icons.Material.Outlined.Dns),
    ];

    private static IRenderedComponent<ChoiceCards<Mode>> Render(DashboardTestContext context, Mode value, List<Mode> changes, bool disabled = false) =>
        context.Render<ChoiceCards<Mode>>(parameters => parameters
            .Add(p => p.Options, Options)
            .Add(p => p.Value, value)
            .Add(p => p.ValueChanged, mode => changes.Add(mode))
            .Add(p => p.AriaLabel, "Import mode")
            .Add(p => p.DataField, "mode")
            .Add(p => p.Disabled, disabled));

    [Fact]
    public async Task ItIsARadioGroupOfCards_TitleThenHelp_WithTheChosenOneTheOnlyTabStop()
    {
        await using DashboardTestContext context = new();

        var group = Render(context, Mode.Replace, []);

        var root = group.Find("[data-field=mode]");
        Assert.Equal("radiogroup", root.GetAttribute("role"));
        Assert.Equal("Import mode", root.GetAttribute("aria-label"));
        var cards = group.FindAll("[role=radio]");
        Assert.Equal(["false", "true"], cards.Select(c => c.GetAttribute("aria-checked")));
        Assert.Equal(["-1", "0"], cards.Select(c => c.GetAttribute("tabindex")));
        Assert.Equal(["merge", "swap"], cards.Select(c => c.GetAttribute("data-value")));
        Assert.Equal("Merge", cards[0].QuerySelector(".pdk-choice-title")!.TextContent);
        Assert.Equal("Adds what the file names.", cards[0].QuerySelector(".pdk-choice-help")!.TextContent);
        Assert.Null(cards[1].QuerySelector(".pdk-choice-help"));
        Assert.NotNull(cards[1].QuerySelector(".pdk-choice-icon"));
        Assert.Contains("pdk-selected", cards[1].ClassList);
    }

    [Fact]
    public async Task AClickOrAnArrowKeyChoosesAnotherCard_AndTheChosenOneIsNotChosenAgain()
    {
        await using DashboardTestContext context = new();
        List<Mode> changes = [];
        var group = Render(context, Mode.Merge, changes);

        await group.Find("[data-value=merge]").ClickAsync(new());
        await group.Find("[data-value=swap]").ClickAsync(new());
        await group.Find("[data-value=merge]").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowRight" });
        await group.Find("[data-value=merge]").KeyDownAsync(new KeyboardEventArgs { Key = "x" });

        Assert.Equal([Mode.Replace, Mode.Replace], changes);
    }

    [Fact]
    public async Task ADisabledGroupChoosesNothing()
    {
        await using DashboardTestContext context = new();
        List<Mode> changes = [];
        var group = Render(context, Mode.Merge, changes, disabled: true);

        await group.Find("[data-value=merge]").KeyDownAsync(new KeyboardEventArgs { Key = "End" });

        Assert.Empty(changes);
        Assert.Equal("true", group.Find("[role=radiogroup]").GetAttribute("aria-disabled"));
        Assert.All(group.FindAll("[role=radio]"), c => Assert.True(c.HasAttribute("disabled")));
    }
}
