using System.Net;
using System.Text;
using System.Text.Json;

using Paduraksa.Application.Cloudflare;
using Paduraksa.Proxy.Net;

namespace Paduraksa.Fuzz;

/// <summary>
/// Cloudflare's edge ranges as the refresh job reads them off the network (PLAN §7, trusted proxies): whatever the host named in the options answers, through
/// <see cref="CloudflareRangeFetcher"/> and <see cref="EdgeRangeList.TryCreate"/>, with no socket (the handler answers). The first input byte picks what answers where (the JSON endpoint, the two
/// plain-text ones, or both), the rest is the body; the text endpoints get the body cut at its first NUL, a list each. Properties: no exception, and exactly a list or a reason;
/// the same answer twice; a body past the size cap is never read into a list; a list that is accepted holds only blocks that an independent reading of the rules accepts (each
/// canonical, sorted, unique, none wider than the shortest prefix, three of each family at least, 512 at most), is the blocks the body gave and nothing else, and a set that contains every block's ends;
/// anything the rules refuse is refused whole; and a reason is short and printable (no control, format or line-separating character), since it is logged and shown (the first thing this target found: an escape sequence in a block came through).
/// </summary>
internal static class CloudflareRangesTarget
{
    private const int BodyCap = 1024;

    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private static readonly CloudflareRangeRefreshOptions Options = new()
    {
        ApiUrl = new Uri("https://api.fuzz.test/client/v4/ips"),
        TextV4Url = new Uri("https://text.fuzz.test/ips-v4"),
        TextV6Url = new Uri("https://text.fuzz.test/ips-v6"),
        MaxBodyBytes = BodyCap,
        RequestTimeout = TimeSpan.FromSeconds(30),
    };

    public static FuzzTarget Target { get; } = new(
        "cloudflare-ranges",
        "Cloudflare's edge range list as fetched: the JSON and plain-text answers, the size cap, and the checks that make a list believable",
        Run,
        Seeds);

    private static void Run(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        var mode = input[0] & 3;
        var body = input[1..].ToArray();
        var nul = Array.IndexOf(body, (byte)0);
        var v4 = nul < 0 ? body : body[..nul];
        var v6 = nul < 0 ? [] : body[(nul + 1)..];
        var api = mode != 1 ? body : null;
        var text = mode != 0 ? (v4, v6) : ((byte[], byte[])?)null;

        var (list, failure) = Fetch(api, text);
        var again = Fetch(api, text);
        Checks.That(Describe(list, failure) == Describe(again.List, again.Failure), "the same answers gave two different results");
        Checks.That((list is null) != (failure is null), "a result must be a list or a reason, exactly one");

        // What the rules say, read independently: the first source that gives a list wins, the JSON before the text.
        var expected = api is { Length: <= BodyCap } json && FromJson(json) is { } fromApi ? fromApi
            : text is ({ Length: <= BodyCap } a, { Length: <= BodyCap } b) ? FromText(a, b)
            : null;

        if (list is null)
        {
            Checks.That(expected is null, $"the rules accept the answers ({expected?.Count} blocks) and the fetcher refused them: {failure}");
            Checks.That(failure!.Length <= 1024 && !failure.Any(static c => char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator), $"a refusal that is too long or has a control character: '{failure}'");
            return;
        }

        Checks.That(expected is not null, $"the fetcher accepted a list of {list.Count} blocks that the rules refuse");
        Checks.That(list.Blocks.SequenceEqual(expected!), $"the list is not what the body says: {string.Join(' ', list.Blocks.Take(4))}...");
        Checks.That(list.Origin == EdgeRangeOrigin.Fetched && list.FetchedAt == Now, "the list carries the wrong origin or time");
        Checks.That(list.Count is >= (2 * EdgeRangeList.MinimumPerFamily) and <= EdgeRangeList.MaximumEntries, $"{list.Count} blocks is not a believable list");
        int v4Count = 0, v6Count = 0;
        foreach (var block in list.Blocks)
        {
            Checks.That(IpNetwork.TryParse(block, out var network, out var error), $"'{block}' is in the list and is not a CIDR block: {error}");
            Checks.That(block.Contains('/', StringComparison.Ordinal) && network.ToString() == block, $"'{block}' is not the canonical text of {network}");
            Checks.That(network.PrefixLength >= (network.IsIPv4 ? EdgeRangeList.ShortestIPv4Prefix : EdgeRangeList.ShortestIPv6Prefix), $"'{block}' is wider than the list may hold");
            Checks.That(list.Ranges.Contains(network.First) && list.Ranges.Contains(network.Last), $"the set doesn't contain the ends of {block}");
            _ = network.IsIPv4 ? v4Count++ : v6Count++;
        }

        Checks.That(v4Count >= EdgeRangeList.MinimumPerFamily && v6Count >= EdgeRangeList.MinimumPerFamily, $"{v4Count} IPv4 and {v6Count} IPv6 blocks were believed");
    }

    private static (EdgeRangeList? List, string? Failure) Fetch(byte[]? api, (byte[] V4, byte[] V6)? text)
    {
        using HttpClient http = new(new Answers(api, text));
        return new CloudflareRangeFetcher(http, Options, new FixedTime()).FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private static string Describe(EdgeRangeList? list, string? failure) => list is null ? "refused: " + failure : string.Join(' ', list.Blocks);

    /// <summary>The blocks of the JSON answer as the rules want them, or null when the answer is not an acceptable list.</summary>
    private static SortedSet<string>? FromJson(byte[] body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || (root.TryGetProperty("success", out var success) && success.ValueKind != JsonValueKind.True)
                || !root.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            List<string> blocks = [];
            foreach (var name in (string[])["ipv4_cidrs", "ipv6_cidrs"])
            {
                if (!result.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                try
                {
                    blocks.AddRange(array.EnumerateArray().Select(static entry => entry.ValueKind == JsonValueKind.String ? entry.GetString()! : string.Empty));
                }
                catch (InvalidOperationException)
                {
                    // Text that is not UTF-8: the answer is refused (the fetcher used to throw here, and end the fetch with the text endpoints unasked).
                    return null;
                }
            }

            return Judge(blocks);
        }
    }

    private static SortedSet<string>? FromText(byte[] v4, byte[] v6) =>
        Judge([.. Lines(v4), .. Lines(v6)]);

    private static IEnumerable<string> Lines(byte[] body) =>
        Encoding.UTF8.GetString(body).Split('\n').Select(static line => line.Trim()).Where(static line => line.Length > 0);

    /// <summary>The list rules, written out again: every entry a CIDR block with a prefix, none too wide, three of each family, no more than 512 once duplicates are gone.</summary>
    private static SortedSet<string>? Judge(List<string> blocks)
    {
        SortedSet<string> unique = new(StringComparer.Ordinal);
        int v4 = 0, v6 = 0;
        foreach (var raw in blocks)
        {
            var text = raw.Trim();
            if (!text.Contains('/', StringComparison.Ordinal) || !IpNetwork.TryParse(text, out var network))
            {
                return null;
            }

            if (network.PrefixLength < (network.IsIPv4 ? 8 : 12))
            {
                return null;
            }

            if (unique.Add(network.ToString()))
            {
                _ = network.IsIPv4 ? v4++ : v6++;
                if (unique.Count > 512)
                {
                    return null;
                }
            }
        }

        return v4 >= 3 && v6 >= 3 ? unique : null;
    }

    /// <summary>The network as the fetcher sees it: each URL answers with its own bytes, or 404.</summary>
    private sealed class Answers(byte[]? api, (byte[] V4, byte[] V6)? text) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            var body = url == Options.ApiUrl ? api : url == Options.TextV4Url ? text?.V4 : url == Options.TextV6Url ? text?.V6 : null;
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static IEnumerable<(string Name, byte[] Bytes)> Seeds()
    {
        static byte[] Case(int mode, string body) => [(byte)mode, .. Encoding.UTF8.GetBytes(body)];

        string[] v4 = ["173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "104.16.0.0/13"];
        string[] v6 = ["2400:cb00::/32", "2606:4700::/32", "2803:f800::/32", "2a06:98c0::/29"];
        static string Json(string[] first, string[] second, string success = "true") =>
            "{\"result\":{\"ipv4_cidrs\":[" + string.Join(',', first.Select(static b => "\"" + b + "\"")) + "],\"ipv6_cidrs\":[" + string.Join(',', second.Select(static b => "\"" + b + "\"")) + "],\"etag\":\"x\"},\"success\":" + success + ",\"errors\":[],\"messages\":[]}";

        yield return ("api", Case(0, Json(v4, v6)));
        yield return ("api-unsuccessful", Case(0, Json(v4, v6, "false")));
        yield return ("api-no-ipv6", Case(0, Json(v4, [])));
        yield return ("api-one-entry-wide", Case(0, Json([.. v4, "0.0.0.0/0"], v6)));
        yield return ("api-host-bits", Case(0, Json([.. v4, "173.245.48.1/20"], v6)));
        yield return ("api-not-a-string", Case(0, "{\"result\":{\"ipv4_cidrs\":[1,2,3],\"ipv6_cidrs\":[]},\"success\":true}"));
        yield return ("api-no-result", Case(0, "{\"success\":true}"));
        yield return ("api-array", Case(0, "[]"));
        yield return ("text", [1, .. Encoding.UTF8.GetBytes(string.Join('\n', v4)), 0, .. Encoding.UTF8.GetBytes(string.Join('\n', v6) + "\n")]);
        yield return ("text-bare-address", [1, .. Encoding.UTF8.GetBytes(string.Join('\n', [.. v4, "1.1.1.1"])), 0, .. Encoding.UTF8.GetBytes(string.Join('\n', v6))]);
        yield return ("text-crlf", [1, .. Encoding.UTF8.GetBytes(string.Join("\r\n", v4)), 0, .. Encoding.UTF8.GetBytes(string.Join("\r\n", v6))]);
        yield return ("text-too-few", [1, .. Encoding.UTF8.GetBytes(string.Join('\n', v4.Take(2))), 0, .. Encoding.UTF8.GetBytes(string.Join('\n', v6))]);
        yield return ("api-then-text", [2, .. Encoding.UTF8.GetBytes(string.Join('\n', v4)), 0, .. Encoding.UTF8.GetBytes(string.Join('\n', v6))]);
        yield return ("html", Case(2, "<html><body>Service Unavailable</body></html>"));
    }
}
