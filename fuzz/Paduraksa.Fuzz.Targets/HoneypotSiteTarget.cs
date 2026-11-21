using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Paduraksa.Proxy.Honeypot;

namespace Paduraksa.Fuzz;

/// <summary>
/// The trap site's two attacker-facing sides, chosen by the first byte. <b>Resolve</b>: the rest is a path inside the prefix, and <see cref="HoneypotSite.Resolve"/> must answer exactly
/// what an independent reading of the URL scheme and the token layout answers (base32 in a <see cref="BigInteger"/>, the head unmasked, the tag from <see cref="HMACSHA256"/> over the
/// fields and the URL text), in bounded time and without an exception; anything it accepts renders. <b>Canary</b>: any IP and user agent give two lower-case words, the same each time.
/// <b>Render</b>: a target built from the bytes (a page kind that may not exist, any IDs) and a canary of any characters renders to the same bytes twice, with a status of 200 or 404 and a
/// bounded size, and the same bytes as the canary with everything but letters, digits and spaces taken out.
/// </summary>
internal static partial class HoneypotSiteTarget
{
    private const string Prefix = "/archive-k7f3/";

    private static readonly byte[] Key = [.. Enumerable.Range(0, 32).Select(static i => (byte)((i * 13) + 5))];
    private static readonly HoneypotSite Site = new(Key, new HoneypotSiteOptions(Prefix, "Quiet Notes", 25));

    public static FuzzTarget Target { get; } = new(
        "honeypot-site",
        "the trap site: URL and token resolution against an independent reading, the canary, and rendering any target",
        Run,
        Seeds);

    private static void Run(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        var rest = input[1..];
        switch (input[0] % 3)
        {
            case 0:
                ResolveCheck(rest);
                break;
            case 1:
                CanaryCheck(rest);
                break;
            default:
                RenderCheck(rest);
                break;
        }
    }

    private static void ResolveCheck(ReadOnlySpan<byte> bytes)
    {
        var path = Prefix + Encoding.Latin1.GetString(bytes[..Math.Min(bytes.Length, 1200)]);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var target = Checks.Deterministic(() => Site.Resolve(path), "the resolved target");

        // One HMAC over at most 132 bytes; a second means the work grew with the input.
        Checks.That(watch.Elapsed < TimeSpan.FromSeconds(1), $"resolving {path.Length} characters took {watch.Elapsed.TotalMilliseconds:F0} ms");
        var reference = Reference(path);
        Checks.That(target == reference, $"'{Shown(path)}': the site says {target}, the independent reading says {reference}");
        if (target.Kind != HoneypotPageKind.NotFound)
        {
            var writer = new ArrayBufferWriter<byte>();
            var render = Site.Render(target, string.Empty, writer);
            Checks.That(render.Status == 200 && writer.WrittenCount > 0, "a resolved target did not render");
        }
    }

    private static void CanaryCheck(ReadOnlySpan<byte> bytes)
    {
        var ipLength = bytes.IsEmpty ? 0 : bytes[0] % 20;
        var ip = bytes.IsEmpty ? default : bytes.Slice(1, Math.Min(ipLength, bytes.Length - 1));
        var agent = Encoding.Latin1.GetString(bytes[Math.Min(bytes.Length, 1 + ip.Length)..]);
        var ipBytes = ip.ToArray();
        var canary = Checks.Deterministic(() => Site.Canary(ipBytes, agent), "the canary");
        Checks.That(CanaryShape().IsMatch(canary), $"'{canary}' is not two lower-case words");
    }

    private static void RenderCheck(ReadOnlySpan<byte> bytes)
    {
        Span<byte> head = stackalloc byte[26];
        bytes[..Math.Min(bytes.Length, head.Length)].CopyTo(head);
        var kind = (HoneypotPageKind)(head[0] % 12);
        var via = (HoneypotLinkKind)(head[1] % 13);
        var target = new HoneypotTarget(kind, via, BinaryPrimitives.ReadUInt64LittleEndian(head[2..]), BinaryPrimitives.ReadUInt64LittleEndian(head[10..]), (head[18] & 1) == 1);
        var canary = Encoding.Latin1.GetString(bytes[Math.Min(bytes.Length, head.Length)..][..Math.Min(Math.Max(bytes.Length - head.Length, 0), 120)]);

        var first = new ArrayBufferWriter<byte>();
        var render = Site.Render(target, canary, first);
        var second = new ArrayBufferWriter<byte>();
        _ = Site.Render(target, canary, second);
        Checks.That(first.WrittenSpan.SequenceEqual(second.WrittenSpan), $"{target} rendered two different pages");
        Checks.That(render.Status is 200 or 404, $"status {render.Status}");
        Checks.That(first.WrittenCount is > 0 and < 256 * 1024, $"{first.WrittenCount} bytes");

        // Only letters, digits and spaces of a canary reach the page, so a canary and its filtered self are the same page.
        var filtered = new StringBuilder();
        foreach (var c in canary)
        {
            if (filtered.Length < 64 && c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or ' ')
            {
                _ = filtered.Append(c);
            }
        }

        var plain = new ArrayBufferWriter<byte>();
        _ = Site.Render(target, filtered.ToString(), plain);
        Checks.That(first.WrittenSpan.SequenceEqual(plain.WrittenSpan), "a canary's other characters changed the page");
    }

    // ---- the independent reading ----

    private static HoneypotTarget NotFound { get; } = new(HoneypotPageKind.NotFound, HoneypotLinkKind.Direct, HoneypotSite.Pack(HoneypotPageKind.NotFound, 0), 0, false);

    private static HoneypotTarget Reference(string path)
    {
        if (!path.StartsWith(Prefix, StringComparison.Ordinal) || path.Length - Prefix.Length > 256)
        {
            return NotFound;
        }

        var rest = path[Prefix.Length..];
        static HoneypotTarget Direct(HoneypotPageKind kind, uint local) => new(kind, HoneypotLinkKind.Direct, HoneypotSite.Pack(kind, local), 0, Site.NeedsPow(kind, HoneypotSite.Pack(kind, local)));

        if (rest.Length == 0)
        {
            return Direct(HoneypotPageKind.Index, 0);
        }

        if (rest == "sitemap.xml")
        {
            return Direct(HoneypotPageKind.Sitemap, 0);
        }

        if (rest == "feed/")
        {
            return Direct(HoneypotPageKind.Feed, 0);
        }

        var pageNumber = PageNumber().Match(rest);
        if (rest.StartsWith("page/", StringComparison.Ordinal))
        {
            return pageNumber.Success && uint.Parse(pageNumber.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) is >= 2 and <= 99_999
                ? Direct(HoneypotPageKind.IndexPage, uint.Parse(pageNumber.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
                : NotFound;
        }

        var (kind, body, count) = rest switch
        {
            _ when rest.StartsWith("category/", StringComparison.Ordinal) && rest.EndsWith('/') => (HoneypotPageKind.Category, rest[..^1], 8),
            _ when rest.StartsWith("tag/", StringComparison.Ordinal) && rest.EndsWith('/') => (HoneypotPageKind.Tag, rest[..^1], 24),
            _ when rest.StartsWith("author/", StringComparison.Ordinal) && rest.EndsWith('/') => (HoneypotPageKind.Author, rest[..^1], 4),
            _ when rest.StartsWith("media/", StringComparison.Ordinal) && rest.EndsWith(".svg", StringComparison.Ordinal) => (HoneypotPageKind.Image, rest[..^4], int.MaxValue),
            _ when rest.StartsWith("assets/stats-", StringComparison.Ordinal) && rest.EndsWith(".json", StringComparison.Ordinal) => (HoneypotPageKind.Beacon, rest[..^5], int.MaxValue),
            _ when PostPath().IsMatch(rest) => (HoneypotPageKind.Post, rest[..^1], int.MaxValue),
            _ => (HoneypotPageKind.NotFound, string.Empty, 0),
        };
        if (kind == HoneypotPageKind.NotFound || body.Length <= 21 || body[^21] != '-' || body.Length - 21 > 120)
        {
            return NotFound;
        }

        var text = body[..^21];
        var token = body[^20..];
        if (text.Any(static c => c is < '!' or > '~') || !Base32Token().IsMatch(token))
        {
            return NotFound;
        }

        // 20 base32 characters are 100 bits: 64 of masked head and 36 of tag.
        var raw = BigInteger.Zero;
        foreach (var c in token)
        {
            raw = (raw << 5) + (c <= 'z' && c >= 'a' ? c - 'a' : c - '2' + 26);
        }

        var tag = (ulong)(raw & ((BigInteger.One << 36) - 1));
        var head = (ulong)(raw >> 36) ^ SplitMix(tag ^ Site.TokenSalt);
        var via = (int)(head >> 60);
        var parentKind = (int)((head >> 56) & 15);
        var parentLocal = (uint)((head >> 28) & 0xFFFFFFF);
        var local = (uint)(head & 0xFFFFFFF);
        if (via is 0 or > 12 || parentKind > 11)
        {
            return NotFound;
        }

        var message = new byte[12 + text.Length];
        message[0] = 1;
        message[1] = (byte)kind;
        message[2] = (byte)via;
        message[3] = (byte)parentKind;
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(4), local);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(8), parentLocal);
        Encoding.ASCII.GetBytes(text, message.AsSpan(12));
        var expected = BinaryPrimitives.ReadUInt64BigEndian(HMACSHA256.HashData(Key, message)) >> 28;
        if (expected != tag || local >= count)
        {
            return NotFound;
        }

        var id = HoneypotSite.Pack(kind, local);
        return new HoneypotTarget(kind, (HoneypotLinkKind)via, id, HoneypotSite.Pack((HoneypotPageKind)parentKind, parentLocal), Site.NeedsPow(kind, id));
    }

    private static ulong SplitMix(ulong value)
    {
        value += 0x9E3779B97F4A7C15UL;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    private static string Shown(string text) => text.Length <= 80 ? text : string.Concat(text.AsSpan(0, 80), "...");

    [GeneratedRegex("^page/([1-9][0-9]{0,4})/$")]
    private static partial Regex PageNumber();

    [GeneratedRegex("^[0-9]{4}/[0-9]{2}/.*/$", RegexOptions.Singleline)]
    private static partial Regex PostPath();

    [GeneratedRegex("^[a-z2-7]{20}$")]
    private static partial Regex Base32Token();

    [GeneratedRegex("^[a-z]+ [a-z]+$")]
    private static partial Regex CanaryShape();

    // ---- seeds ----

    private static IEnumerable<(string Name, byte[] Bytes)> Seeds()
    {
        static (string, byte[]) Resolve(string name, string path) => (name, [0, .. Encoding.Latin1.GetBytes(path[Prefix.Length..])]);

        var index = Render(Prefix);
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(index, "(?:href|src)=\"([^\"]+)\"").Cast<Match>().Concat(Regex.Matches(index, "fetch\\(\"([^\"]*)\"\\+\"([^\"]*)\"")))
        {
            var url = m.Groups.Count > 2 ? m.Groups[1].Value + m.Groups[2].Value : m.Groups[1].Value;
            var key = Site.Resolve(url).Kind.ToString();
            _ = links.TryAdd(key, url);
        }

        foreach (var (kind, url) in links)
        {
            yield return Resolve("resolve-" + kind.ToLowerInvariant(), url);
        }

        var post = links["Post"];
        yield return Resolve("tampered-post", post[..^3] + (post[^3] == 'a' ? "b" : "a") + post[^2..]);
        yield return Resolve("page-2", Prefix + "page/2/");
        yield return Resolve("page-0", Prefix + "page/0/");
        yield return Resolve("sitemap", Prefix + "sitemap.xml");
        yield return Resolve("feed", Prefix + "feed/");
        yield return Resolve("index", Prefix);
        yield return Resolve("long-garbage", Prefix + new string('a', 400));
        yield return ("canary", [1, 4, 203, 0, 113, 9, .. "Mozilla/5.0"u8]);
        yield return ("canary-empty", [1]);
        yield return ("render-post", [2, (byte)HoneypotPageKind.Post, 1, 9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, .. "fox trot"u8]);
        yield return ("render-hostile-canary", [2, (byte)HoneypotPageKind.Landing, 1, 9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, .. "<script>\"&'"u8]);
        yield return ("render-missing-category", [2, (byte)HoneypotPageKind.Category, 1, 200, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
    }

    private static string Render(string path)
    {
        var writer = new ArrayBufferWriter<byte>();
        _ = Site.Render(Site.Resolve(path), string.Empty, writer);
        return Encoding.UTF8.GetString(writer.WrittenSpan);
    }
}
