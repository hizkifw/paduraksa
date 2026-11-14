using System.Globalization;

using Paduraksa.Contracts.Logging;

namespace Paduraksa.Dashboard.LogFiles;

/// <summary>One bar of a request's waterfall: where it starts and how long it took, in milliseconds from the top of the request.</summary>
/// <param name="Key">The stage, for tests and the bar's colour (<c>gate</c>, <c>connect</c>, <c>body</c>, <c>wait</c>, <c>send</c>).</param>
/// <param name="Label">What the row says.</param>
/// <param name="StartMs">Where the bar starts.</param>
/// <param name="Ms">How long the stage took.</param>
public sealed record WaterfallStage(string Key, string Label, double StartMs, double Ms);

/// <summary>
/// Lays a live-tail entry's stages out on one axis from 0 to its total, as Chrome DevTools' Timing tab does
/// (docs/design/observability.md "Request timing"): each bar at its measured start with its measured length, the rows in the order the
/// stages started. Stages overlap (the body goes up while the forward runs), and time no stage accounts for stays a gap.
/// </summary>
public static class RequestWaterfall
{
    public static IReadOnlyList<WaterfallStage> Stages(LogTailEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Timing is not { } timing)
        {
            return [];
        }

        // In the pipeline's order, which breaks a tie between two stages that started together.
        List<WaterfallStage> stages = [];
        if (timing.GateMs is { } gate)
        {
            stages.Add(new("gate", "Gate", 0, gate));
        }

        if (timing is { UpstreamConnectMs: > 0 and var connect, UpstreamConnectStartMs: { } connectStart })
        {
            stages.Add(new("connect", "Upstream connect", connectStart, connect));
        }

        if (timing.RequestBodyStartMs is { } bodyStart)
        {
            stages.Add(new("body", "Request body", bodyStart, timing.RequestBodyMs ?? 0));
        }

        if (timing is { UpstreamWaitMs: { } wait, UpstreamWaitStartMs: { } waitStart })
        {
            stages.Add(new("wait", "Waiting for upstream (TTFB)", waitStart, wait));
        }

        if (timing is { ResponseSendMs: { } send, ResponseSendStartMs: { } sendStart })
        {
            stages.Add(new("send", "Response send", sendStart, send));
        }

        return [.. stages.OrderBy(static stage => stage.StartMs)];
    }

    /// <summary>The bar's place on the track as CSS: its offset and width in percent of <paramref name="scaleMs"/>.</summary>
    public static string Geometry(WaterfallStage stage, double scaleMs)
    {
        ArgumentNullException.ThrowIfNull(stage);
        var scale = scaleMs > 0 ? scaleMs : 1;
        var start = Math.Clamp(stage.StartMs / scale * 100, 0, 100);
        var width = Math.Clamp(stage.Ms / scale * 100, 0, 100 - start);
        return string.Create(CultureInfo.InvariantCulture, $"left: {start:0.##}%; width: {width:0.##}%");
    }

    /// <summary>The axis of a waterfall: the request's total, or the furthest a stage reaches when rounding put one past it.</summary>
    public static double Scale(LogTailEntry entry, IReadOnlyList<WaterfallStage> stages)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(stages);
        var reach = entry.TotalMs;
        foreach (var stage in stages)
        {
            reach = Math.Max(reach, stage.StartMs + stage.Ms);
        }

        return reach;
    }
}
