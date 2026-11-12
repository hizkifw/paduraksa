using System.Net.Http;
using System.Text;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Yarp;

namespace Paduraksa.Fuzz;

/// <summary>
/// Whether an upstream's own error answer is replaced by the host's page (PLAN §7.3, "As built (M8)"): a status, the <c>Content-Type</c> lines the upstream sent, a mode
/// (<c>off</c>, <c>html</c>, <c>any</c>), and whether the request upgrades or the response has started. The input is a flag byte (mode, upgrade, started), a 16-bit
/// status, then the <c>Content-Type</c> lines, one per line feed; the lines are read by an <see cref="HttpResponseMessage"/> as the transport would, and the media type it
/// finds is what the decision gets. Properties: no exception; the same verdict for the same bytes and 0 B; and the verdict is what a plain model of the rules says: never for
/// an upgrade, an event stream, <c>application/grpc*</c> or a started response; <c>html</c> only for <c>text/html</c>, whatever its case or parameters; only for a status that has a page.
/// </summary>
internal static class UpstreamErrorReplacementTarget
{
    // The statuses ErrorPageStatuses names, written out so a change to the list has to change the model too.
    private static readonly int[] PageStatuses = [403, 404, 413, 414, 421, 429, 431, 502, 503, 504];

    private static readonly UpstreamErrorReplacement[] Modes = [UpstreamErrorReplacement.Off, UpstreamErrorReplacement.Html, UpstreamErrorReplacement.Any, UpstreamErrorReplacement.Any];

    public static FuzzTarget Target { get; } = new(
        "upstream-error-replacement",
        "whether an upstream's error answer is replaced by the host's page: status, Content-Type, mode, upgrade and started",
        Run,
        Seeds);

    private static void Run(ReadOnlySpan<byte> input)
    {
        if (input.Length < 3)
        {
            return;
        }

        var mode = Modes[input[0] & 3];
        var upgrade = (input[0] & 4) != 0;
        var started = (input[0] & 8) != 0;
        var status = (input[1] | (input[2] << 8)) % 700;
        var lines = Lines(input[3..]);

        using HttpResponseMessage response = new((System.Net.HttpStatusCode)Math.Max(status, 100)) { Content = new ByteArrayContent([]) };
        foreach (var line in lines)
        {
            _ = response.Content.Headers.TryAddWithoutValidation("Content-Type", line);
        }

        var parsed = response.Content.Headers.ContentType?.MediaType;
        if (parsed is not null)
        {
            // What the parser accepted is the first line's text before its parameters, with the blanks it forgives around the slash dropped (a second Content-Type line is not read).
            var before = lines[0].Split(';')[0].Replace(" ", string.Empty, StringComparison.Ordinal).Replace("\t", string.Empty, StringComparison.Ordinal);
            Checks.That(string.Equals(before, parsed, StringComparison.Ordinal), $"'{lines[0]}' gave the media type '{parsed}', not '{before}'");
        }

        var verdict = Checks.Deterministic(() => ReplaceUpstreamErrorTransform.Replaces(mode, status, parsed, upgrade, started), "the replacement verdict");
        Checks.AllocatesNothing(() => _ = ReplaceUpstreamErrorTransform.Replaces(mode, status, parsed, upgrade, started), "ReplaceUpstreamErrorTransform.Replaces");
        Checks.That(
            verdict == Model(mode, status, parsed, upgrade, started),
            $"mode {mode}, status {status}, media type '{parsed}', upgrade {upgrade}, started {started}: replaced {verdict}, a plain reading says {!verdict}");

        // The decision on a media type text the HTTP parser would not hand over (any ASCII), against the same model: the rules don't lean on the parser's choices.
        foreach (var line in lines)
        {
            var media = line.Split(';')[0].Trim(' ', '\t');
            if (media.All(static c => c is >= ' ' and <= '~'))
            {
                Checks.That(
                    ReplaceUpstreamErrorTransform.Replaces(mode, status, media, upgrade, started) == Model(mode, status, media, upgrade, started),
                    $"mode {mode}, status {status}, media type '{media}', upgrade {upgrade}, started {started}: the decision and the plain reading differ");
            }
        }
    }

    // The rules as the PLAN states them, with ASCII-only case folding (a media type is ASCII, and its case does not matter).
    private static bool Model(UpstreamErrorReplacement mode, int status, string? mediaType, bool upgrade, bool started)
    {
        if (mode == UpstreamErrorReplacement.Off || upgrade || started || !PageStatuses.Contains(status))
        {
            return false;
        }

        var media = mediaType is null ? null : AsciiLower(mediaType);
        if (media is "text/event-stream" || (media is not null && media.StartsWith("application/grpc", StringComparison.Ordinal)))
        {
            return false;
        }

        return mode == UpstreamErrorReplacement.Any || media == "text/html";
    }

    private static string AsciiLower(string text) => string.Create(text.Length, text, static (span, source) =>
    {
        for (var i = 0; i < span.Length; i++)
        {
            span[i] = source[i] is >= 'A' and <= 'Z' ? (char)(source[i] + 32) : source[i];
        }
    });

    private static List<string> Lines(ReadOnlySpan<byte> bytes)
    {
        List<string> lines = [];
        while (!bytes.IsEmpty && lines.Count < 4)
        {
            var feed = bytes.IndexOf((byte)'\n');
            lines.Add(Encoding.Latin1.GetString(feed < 0 ? bytes : bytes[..feed]));
            bytes = feed < 0 ? default : bytes[(feed + 1)..];
        }

        return lines;
    }

    private static IEnumerable<(string Name, byte[] Bytes)> Seeds()
    {
        static byte[] Case(int mode, bool upgrade, bool started, int status, params string[] lines) =>
            [(byte)(mode | (upgrade ? 4 : 0) | (started ? 8 : 0)), (byte)(status & 0xFF), (byte)(status >> 8), .. Encoding.Latin1.GetBytes(string.Join('\n', lines))];

        yield return ("html-404-html", Case(1, false, false, 404, "text/html"));
        yield return ("html-404-html-charset", Case(1, false, false, 404, "text/html; charset=utf-8"));
        yield return ("html-404-upper-case", Case(1, false, false, 404, "TEXT/HTML;Charset=UTF-8"));
        yield return ("html-404-json", Case(1, false, false, 404, "application/json"));
        yield return ("html-404-none", Case(1, false, false, 404));
        yield return ("any-502-json", Case(2, false, false, 502, "application/json"));
        yield return ("any-503-none", Case(2, false, false, 503));
        yield return ("any-504-two-lines", Case(2, false, false, 504, "text/html", "text/plain"));
        yield return ("any-500-html", Case(2, false, false, 500, "text/html"));
        yield return ("any-429-html", Case(2, false, false, 429, "text/html"));
        yield return ("any-event-stream", Case(2, false, false, 503, "text/event-stream"));
        yield return ("any-event-stream-charset", Case(2, false, false, 503, "Text/Event-Stream; charset=utf-8"));
        yield return ("any-grpc", Case(2, false, false, 502, "application/grpc"));
        yield return ("any-grpc-proto", Case(2, false, false, 502, "application/grpc+proto"));
        yield return ("any-upgrade", Case(2, true, false, 502, "text/html"));
        yield return ("any-started", Case(2, false, true, 502, "text/html"));
        yield return ("off-404-html", Case(0, false, false, 404, "text/html"));
        yield return ("any-200", Case(2, false, false, 200, "text/html"));
        yield return ("any-not-a-media-type", Case(2, false, false, 413, "not a media type"));
    }
}
