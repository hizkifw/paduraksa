using System.Globalization;

using Paduraksa.Contracts.Logging;

namespace Paduraksa.Dashboard.LogFiles;

/// <summary>One bar of a request's waterfall: where it starts and how long it took, in milliseconds from the top of the request.</summary>
/// <param name="Key">The stage, for tests and the bar's colour (<c>gate</c>, <c>body</c>, <c>connect</c>, <c>wait</c>, <c>send</c>).</param>
/// <param name="Label">What the row says.</param>
/// <param name="StartMs">Where the bar starts.</param>
/// <param name="Ms">How long the stage took.</param>
/// <param name="Reused">A connect that didn't happen: the forward went out on a pooled connection.</param>
public sealed record WaterfallStage(string Key, string Label, double StartMs, double Ms, bool Reused = false);

/// <summary>
/// Lays a live-tail entry's stages out on its total time (docs/design/observability.md "Request timing"). Only the lengths are
/// measured; the starts follow the pipeline's order: the gate from the top, the forward's connect and wait from the gate's hand-off,
/// the body from when it can first be read (once the upstream connection is up, or after the gate for an answer), and the send ending
/// with the request.
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

        List<WaterfallStage> stages = [];
        var gate = timing.GateMs ?? 0;
        if (timing.GateMs is { } gateMs)
        {
            stages.Add(new("gate", "Gate", 0, gateMs));
        }

        var forwarded = timing.UpstreamConnectMs is not null && timing.UpstreamWaitMs is not null;
        var connect = timing.UpstreamConnectMs ?? 0;
        if (timing.RequestBodyMs is > 0 and var body)
        {
            stages.Add(new("body", "Request body", forwarded ? gate + connect : gate, body));
        }

        if (forwarded)
        {
            stages.Add(new("connect", "Upstream connect", gate, connect, Reused: connect <= 0));
            stages.Add(new("wait", "Upstream wait", gate + connect, timing.UpstreamWaitMs!.Value));
        }

        if (timing.ResponseSendMs is { } send)
        {
            stages.Add(new("send", "Response send", Math.Max(0, entry.TotalMs - send), send));
        }

        return stages;
    }

    /// <summary>The bar's place on the track as CSS: its offset and width in percent of <paramref name="totalMs"/>.</summary>
    public static string Geometry(WaterfallStage stage, double totalMs)
    {
        ArgumentNullException.ThrowIfNull(stage);
        var scale = totalMs > 0 ? totalMs : 1;
        var start = Math.Clamp(stage.StartMs / scale * 100, 0, 100);
        var width = Math.Clamp(stage.Ms / scale * 100, 0, 100 - start);
        return string.Create(CultureInfo.InvariantCulture, $"margin-left: {start:0.##}%; width: {width:0.##}%");
    }

    /// <summary>The scale of a waterfall: the request's total, or the furthest a stage reaches when rounding put one past it.</summary>
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
