using Bunit;

using Paduraksa.Dashboard.Components;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>A value that can't be changed is a label over the value, not an input box that looks editable.</summary>
public sealed class ReadOnlyFieldTests
{
    [Fact]
    public async Task ItIsALabelAndAValueWithNoInput()
    {
        await using DashboardTestContext context = new();

        var field = context.Render<ReadOnlyField>(p => p.Add(f => f.Field, "username").Add(f => f.Label, "Username").Add(f => f.Value, "ada").Add(f => f.Mono, true));

        Assert.Empty(field.FindAll("input, textarea"));
        Assert.Equal("Username", field.Find(".pdk-readonly-label").TextContent);
        var value = field.Find("[data-part=value]");
        Assert.Equal("ada", value.TextContent);
        Assert.Contains("pdk-mono", value.ClassName, StringComparison.Ordinal);
        // The value is named by its label, so a screen reader reads them together.
        Assert.Equal(field.Find(".pdk-readonly-label").Id, value.GetAttribute("aria-labelledby"));
        Assert.Equal("true", value.GetAttribute("aria-readonly"));
        Assert.Empty(field.FindAll("[data-part=note]"));
    }

    [Fact]
    public async Task ANoteAndAClassAreOptionalAndAValueIsMonospaceOnlyWhenAsked()
    {
        await using DashboardTestContext context = new();

        var field = context.Render<ReadOnlyField>(p => p.Add(f => f.Field, "display-name").Add(f => f.Label, "Display name").Add(f => f.Value, "Ada")
            .Add(f => f.Note, "An Owner can change it.").Add(f => f.Class, "pdk-setting-control"));

        Assert.Equal("An Owner can change it.", field.Find("[data-part=note]").TextContent);
        Assert.DoesNotContain("pdk-mono", field.Find("[data-part=value]").ClassName, StringComparison.Ordinal);
        Assert.Contains("pdk-setting-control", field.Find("[data-readonly=display-name]").ClassName, StringComparison.Ordinal);
    }
}
