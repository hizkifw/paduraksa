using Bunit;

using Microsoft.AspNetCore.Components;

using MudBlazor;

using Paduraksa.Dashboard.SettingsArea;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>The duration field of the server settings: a typed number and a unit, and what it does with one a span can't hold.</summary>
public sealed class DurationFieldTests
{
    private static IRenderedComponent<DurationField> Render(DashboardTestContext context, List<TimeSpan?> emitted)
    {
        context.Render<MudPopoverProvider>();
        return context.Render<DurationField>(parameters => parameters
            .Add(p => p.Label, "Retention")
            .Add(p => p.Field, "retention")
            .Add(p => p.Default, TimeSpan.FromDays(90))
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<TimeSpan?>(emitted, span => emitted.Add(span))));
    }

    [Fact]
    public async Task ANumberPastWhatASpanHoldsIsSaidAtTheFieldAndNotTakenAsTheDefault()
    {
        await using DashboardTestContext context = new();
        List<TimeSpan?> emitted = [];
        var field = Render(context, emitted);
        var number = field.FindComponent<MudNumericField<double?>>();

        await field.InvokeAsync(() => number.Instance.ValueChanged.InvokeAsync(1e300));

        Assert.Empty(emitted);
        Assert.Contains("Too long to store", field.Markup, StringComparison.Ordinal);

        // A number that fits clears it and is emitted in the unit shown.
        await field.InvokeAsync(() => number.Instance.ValueChanged.InvokeAsync(5));

        Assert.Equal([TimeSpan.FromDays(5)], emitted);
        Assert.DoesNotContain("Too long to store", field.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearingTheFieldEmitsNothingForTheDefault()
    {
        await using DashboardTestContext context = new();
        List<TimeSpan?> emitted = [];
        var field = Render(context, emitted);

        await field.InvokeAsync(() => field.FindComponent<MudNumericField<double?>>().Instance.ValueChanged.InvokeAsync(null));

        Assert.Equal([(TimeSpan?)null], emitted);
    }
}
