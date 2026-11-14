using System.Globalization;

using Paduraksa.Contracts.Logging;

namespace Paduraksa.Dashboard.LogFiles;

/// <summary>One bar of a request's waterfall: where it starts and how long it took, in milliseconds from the top of the request.</summary>
/// <param name="Key">The stage, for tests and the bar's colour (<c>gate</c>, <c>connect</c>, <c>body</c>, <c>wait</c>, <c>send</c>; <c>forward</c>, <c>prepare</c> and <c>gap</c> when derived).</param>
/// <param name="Label">What the row says.</param>
/// <param name="StartMs">Where the bar starts.</param>
/// <param name="Ms">How long the stage took.</param>
/// <param name="Derived">Worked out from the gaps between measured stages, not measured itself.</param>
/// <param name="Shown">The length to display, when it was nudged by a rounding step so the rows add up to the total; <see cref="Ms"/> otherwise.</param>
public sealed record WaterfallStage(string Key, string Label, double StartMs, double Ms, bool Derived = false, double? Shown = null)
{
    public double ShownMs => Shown ?? Ms;

    /// <summary>What the stage is, for the tooltip: the proxy's work in it, and what a long one points at.</summary>
    public string Note => Key switch
    {
        "gate" => "Our checks before forwarding: access lists, rule sets, rate limits, proof of work and sign-in.",
        "connect" => "Opening a new connection to the upstream (DNS, TCP and TLS). A pooled connection skips this.",
        "body" => "Reading the client's upload and passing it on to the upstream.",
        "wait" => "From the request being fully sent to the upstream's response headers: the upstream's own think time plus the network.",
        "send" => "Streaming the response to the client. Slow clients and big downloads show here.",
        "forward" => "Derived. The proxy's own work between stages: building the upstream request and handing it to a connection.",
        "prepare" => "Derived. The proxy's own work between stages: copying the upstream's headers and running the response transforms.",
        RequestWaterfall.GapKey => "Derived. Time not covered by any recorded stage.",
        _ => string.Empty,
    };
}

/// <summary>
/// Lays a live-tail entry's stages out on one axis from 0 to its total, as Chrome DevTools' Timing tab does
/// (docs/design/observability.md "Request timing"): each bar at its measured start with its measured length, the rows in the order the
/// stages started. Stages overlap (the body goes up while the forward runs). The time between them is filled with derived rows worked out
/// from the same figures (the proxy preparing the forward and the response, or time nothing measured), so no stretch of the request is blank.
/// </summary>
public static class RequestWaterfall
{
    public const string GapKey = "gap";

    // Under about a microsecond the figures' own resolution decides, so a gap that small is rounding and not a row.
    private const double SliverMs = 0.00095;

    // A stage's end and the next one's start come from different clocks' rounding; rows that add up to the total within this are rounded to add up exactly.
    private const double SumToleranceMs = 0.005;

    public static IReadOnlyList<WaterfallStage> Stages(LogTailEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Timing is not { } timing)
        {
            return [];
        }

        // In the pipeline's order, which breaks a tie between two stages that started together.
        List<WaterfallStage> measured = [];
        if (timing.GateMs is { } gate)
        {
            measured.Add(new("gate", "Gate", 0, gate));
        }

        if (timing is { UpstreamConnectMs: > 0 and var connect, UpstreamConnectStartMs: { } connectStart })
        {
            measured.Add(new("connect", "Upstream connect", connectStart, connect));
        }

        if (timing.RequestBodyStartMs is { } bodyStart)
        {
            measured.Add(new("body", "Request body", bodyStart, timing.RequestBodyMs ?? 0));
        }

        if (timing is { UpstreamWaitMs: { } wait, UpstreamWaitStartMs: { } waitStart })
        {
            measured.Add(new("wait", "Waiting for upstream (TTFB)", waitStart, wait));
        }

        if (timing is { ResponseSendMs: { } send, ResponseSendStartMs: { } sendStart })
        {
            measured.Add(new("send", "Response send", sendStart, send));
        }

        var stages = new List<WaterfallStage>(measured);
        stages.AddRange(Derive(entry.TotalMs, timing, measured));
        var ordered = stages.OrderBy(static stage => stage.StartMs).ToList();
        RoundToTheTotal(ordered, entry.TotalMs);
        return ordered;
    }

    /// <summary>
    /// The stretches of the request no measured stage covers, as rows. A stretch before the wait's start and after the gate is the proxy
    /// forwarding the request (YARP building and sending it); one between the upstream's answer and the response's start is it preparing the
    /// response (copying the headers, the response transforms). Anything else is unaccounted for.
    /// </summary>
    private static IEnumerable<WaterfallStage> Derive(double totalMs, LogTailTiming timing, List<WaterfallStage> measured)
    {
        // The gate and the answer are cut points even when zero-length, so a gap that spans one is split there and each part is named for its side.
        var upstream = timing is { UpstreamWaitStartMs: { } waitStart, UpstreamWaitMs: { } waitMs } ? (Start: waitStart, End: waitStart + waitMs) : ((double Start, double End)?)null;
        var gateEnd = timing.GateMs ?? 0;
        var sendStart = timing.ResponseSendStartMs;

        List<(double Start, double End)> gaps = [];
        var reach = 0d;
        foreach (var stage in measured.OrderBy(static stage => stage.StartMs))
        {
            if (stage.StartMs - reach >= SliverMs)
            {
                gaps.Add((reach, stage.StartMs));
            }

            reach = Math.Max(reach, stage.StartMs + stage.Ms);
        }

        if (totalMs - reach >= SliverMs)
        {
            gaps.Add((reach, totalMs));
        }

        foreach (var (gapStart, gapEnd) in gaps)
        {
            var cuts = new List<double> { gapStart };
            if (upstream is { } answered)
            {
                cuts.AddRange(new[] { answered.Start, answered.End }.Where(cut => cut > gapStart && cut < gapEnd).Order());
            }

            cuts.Add(gapEnd);
            for (var i = 0; i < cuts.Count - 1; i++)
            {
                var (from, to) = (cuts[i], cuts[i + 1]);
                if (to - from < SliverMs)
                {
                    continue;
                }

                if (upstream is { } up && from >= gateEnd - SliverMs && to <= up.Start + SliverMs)
                {
                    yield return new("forward", "Forwarding the request", from, to - from, Derived: true);
                }
                else if (upstream is { } after && sendStart is { } send && from >= after.End - SliverMs && to <= send + SliverMs)
                {
                    yield return new("prepare", "Preparing the response", from, to - from, Derived: true);
                }
                else
                {
                    yield return new(GapKey, "Unaccounted", from, to - from, Derived: true);
                }
            }
        }
    }

    /// <summary>
    /// Rows that run one after another add up to the total, but each is shown rounded on its own, so the shown parts can miss the shown
    /// total by a step or two. The rounding goes the way each row's remainder says until the parts match, never hiding a row that has time.
    /// Rows that overlap (a body streaming while the upstream answers) can't add up to the total, so those are shown as they are.
    /// </summary>
    private static void RoundToTheTotal(List<WaterfallStage> stages, double totalMs)
    {
        if (stages.Count == 0 || Math.Abs(stages.Sum(static stage => stage.Ms) - totalMs) > SumToleranceMs)
        {
            return;
        }

        var steps = stages.Select(static stage => DisplayFormat.DurationStep(stage.Ms)).ToArray();
        var rounded = stages.Select((stage, i) => Math.Round(stage.Ms / steps[i], MidpointRounding.AwayFromZero) * steps[i]).ToArray();
        var residual = totalMs - rounded.Sum();
        var finest = Enumerable.Range(0, stages.Count).Where(i => stages[i].Ms > 0).Select(i => steps[i]).DefaultIfEmpty(0.01).Min();
        // Each move shifts one row by one of its own steps; a coarse row's rounding can leave many fine steps to give back.
        for (var pass = 0; pass < stages.Count * 10 && Math.Abs(residual) >= finest / 2; pass++)
        {
            var direction = Math.Sign(residual);
            var pick = -1;
            var best = double.NegativeInfinity;
            for (var i = 0; i < stages.Count; i++)
            {
                var moved = rounded[i] + direction * steps[i];
                var keepsItsTime = stages[i].Ms > 0 && moved >= steps[i] - 1e-9;
                if (!keepsItsTime || Math.Abs(residual - direction * steps[i]) >= Math.Abs(residual))
                {
                    continue;
                }

                // How far the row's own value sits past its rounded one in the direction wanted, in its steps.
                var remainder = direction * (stages[i].Ms - rounded[i]) / steps[i];
                if (remainder > best)
                {
                    (best, pick) = (remainder, i);
                }
            }

            if (pick < 0)
            {
                break;
            }

            rounded[pick] += direction * steps[pick];
            residual -= direction * steps[pick];
        }

        for (var i = 0; i < stages.Count; i++)
        {
            stages[i] = stages[i] with { Shown = rounded[i] };
        }
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
