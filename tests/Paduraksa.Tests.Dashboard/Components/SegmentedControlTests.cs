using Bunit;

using Microsoft.AspNetCore.Components.Web;

using Paduraksa.Dashboard.Components;

namespace Paduraksa.Tests.Dashboard.Components;

public sealed class SegmentedControlTests
{
    public enum Scale
    {
        Small,
        Medium,
        Large,
    }

    private static readonly IReadOnlyList<SegmentOption<Scale>> Options =
    [
        new(Scale.Small, "S", Tooltip: "Small"),
        new(Scale.Medium, "M"),
        new(Scale.Large, "L", Key: "big"),
    ];

    private static IRenderedComponent<SegmentedControl<Scale>> Render(DashboardTestContext context, Scale value, List<Scale> changes, bool disabled = false) =>
        context.Render<SegmentedControl<Scale>>(parameters => parameters
            .Add(p => p.Options, Options)
            .Add(p => p.Value, value)
            .Add(p => p.ValueChanged, size => changes.Add(size))
            .Add(p => p.AriaLabel, "Size")
            .Add(p => p.DataField, "size")
            .Add(p => p.Disabled, disabled));

    [Fact]
    public async Task ItIsARadioGroup_WithOneCheckedSegmentThatIsTheOnlyTabStop()
    {
        await using DashboardTestContext context = new();

        var group = Render(context, Scale.Medium, []);

        var root = group.Find("[data-field=size]");
        Assert.Equal("radiogroup", root.GetAttribute("role"));
        Assert.Equal("Size", root.GetAttribute("aria-label"));
        var radios = group.FindAll("[role=radio]");
        Assert.Equal(["false", "true", "false"], radios.Select(r => r.GetAttribute("aria-checked")));
        Assert.Equal(["-1", "0", "-1"], radios.Select(r => r.GetAttribute("tabindex")));
        Assert.Equal(["small", "medium", "big"], radios.Select(r => r.GetAttribute("data-value")));
        Assert.Equal("Small", radios[0].GetAttribute("title"));
    }

    [Fact]
    public async Task WithNothingSelected_TheFirstSegmentTakesTab()
    {
        await using DashboardTestContext context = new();

        var group = Render(context, (Scale)99, []);

        Assert.Equal(["0", "-1", "-1"], group.FindAll("[role=radio]").Select(r => r.GetAttribute("tabindex")));
        Assert.All(group.FindAll("[role=radio]"), r => Assert.Equal("false", r.GetAttribute("aria-checked")));
    }

    [Fact]
    public async Task AClickChoosesASegment_ButNotTheOneAlreadyChosen()
    {
        await using DashboardTestContext context = new();
        List<Scale> changes = [];
        var group = Render(context, Scale.Medium, changes);

        await group.Find("[data-value=medium]").ClickAsync(new());
        await group.Find("[data-value=big]").ClickAsync(new());

        Assert.Equal([Scale.Large], changes);
    }

    [Theory]
    [InlineData("ArrowRight", Scale.Large)]
    [InlineData("ArrowDown", Scale.Large)]
    [InlineData("ArrowLeft", Scale.Small)]
    [InlineData("ArrowUp", Scale.Small)]
    [InlineData("Home", Scale.Small)]
    [InlineData("End", Scale.Large)]
    public async Task TheArrowKeysMoveTheChoice(string key, Scale expected)
    {
        await using DashboardTestContext context = new();
        List<Scale> changes = [];
        var group = Render(context, Scale.Medium, changes);

        await group.Find("[data-value=medium]").KeyDownAsync(new KeyboardEventArgs { Key = key });

        Assert.Equal([expected], changes);
    }

    [Fact]
    public async Task TheArrowKeysWrapAround_AndOtherKeysDoNothing()
    {
        await using DashboardTestContext context = new();
        List<Scale> changes = [];
        var group = Render(context, Scale.Large, changes);

        await group.Find("[data-value=big]").KeyDownAsync(new KeyboardEventArgs { Key = "a" });
        await group.Find("[data-value=big]").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowRight" });

        Assert.Equal([Scale.Small], changes);
    }

    [Fact]
    public async Task ADisabledControlChoosesNothing()
    {
        await using DashboardTestContext context = new();
        List<Scale> changes = [];
        var group = Render(context, Scale.Medium, changes, disabled: true);

        await group.Find("[data-value=big]").KeyDownAsync(new KeyboardEventArgs { Key = "End" });

        Assert.Empty(changes);
        Assert.Equal("true", group.Find("[role=radiogroup]").GetAttribute("aria-disabled"));
        Assert.All(group.FindAll("[role=radio]"), r => Assert.True(r.HasAttribute("disabled")));
    }

    [Fact]
    public async Task ASegmentCanShowACountAndAToneDot_AZeroReadsMuted()
    {
        await using DashboardTestContext context = new();

        var group = context.Render<SegmentedControl<string>>(parameters => parameters
            .Add(p => p.Options, new SegmentOption<string>[] { new("all", "All", Count: 3), new("failed", "Failed", Count: 0, Tone: "error") })
            .Add(p => p.Value, "all")
            .Add(p => p.AriaLabel, "Show only"));

        Assert.Equal("3", group.Find("[data-value=all] .pdk-seg-count").TextContent);
        Assert.Empty(group.FindAll("[data-value=all] .pdk-seg-dot"));
        Assert.Contains("pdk-tone-error", group.Find("[data-value=failed] .pdk-seg-dot").ClassList);
        Assert.Contains("pdk-seg-zero", group.Find("[data-value=failed] .pdk-seg-count").ClassList);
    }
}
