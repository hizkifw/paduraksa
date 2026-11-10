using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;

using Microsoft.Extensions.Primitives;

using Paduraksa.Proxy.Gate;

namespace Paduraksa.Proxy.Pages;

/// <summary>
/// One of the proxy's own pages, built once: the shared layout (doctype, meta, brand style, logo, centred card and
/// footer) is pre-split into byte segments around named slots, and the CSP is hashed from the inline style and script.
/// Writing a page copies those segments into <see cref="IBufferWriter{Byte}"/> and HTML-encodes each slot straight into
/// it, so a request allocates nothing for the page itself.
/// </summary>
/// <remarks>
/// <para>Slots are written in the order they are declared and appear in the body markup exactly once, as
/// <c>{name}</c>. The markup is trusted, static text; only the slot values are encoded, with
/// <see cref="HtmlEncoder"/>'s span overloads.</para>
/// <para>The CSP has no external source: a page without a script allows only its style hash and no form action; a page
/// with a form adds <c>form-action 'self'</c>, and the proof-of-work page adds <c>worker-src blob:</c> for its worker
/// <c>Blob</c> URLs. The style and script are hashed exactly as they are inlined, so the browser's check and the test's
/// recomputation see the same bytes.</para>
/// </remarks>
public sealed class HtmlPageTemplate
{
    /// <summary>The page's content type; UTF-8, though every byte of every built-in page is ASCII.</summary>
    public const string ContentType = "text/html; charset=utf-8";

    /// <summary>
    /// The brand's colours (the dashboard's favicon), light and dark through <c>prefers-color-scheme</c>, so a page the
    /// gate answers reads as Paduraksa's own. The error pages' status line and request ID share it.
    /// </summary>
    public const string BrandStyle = """
        :root{color-scheme:light dark;--bg:#f5f5f3;--fg:#111110;--muted:#5d5c58;--track:#e2e1dc}@media (prefers-color-scheme:dark){:root{--bg:#111110;--fg:#e7e5e0;--muted:#9a988f;--track:#2b2b29}}html{font-family:system-ui,-apple-system,"Segoe UI",sans-serif;line-height:1.5}body{margin:0;min-height:100vh;display:grid;place-items:center;background:var(--bg);color:var(--fg)}main{display:flex;flex-direction:column;align-items:center;gap:.6rem;max-width:22rem;padding:2rem;text-align:center}svg{width:44px;height:44px;margin-bottom:.4rem}h1{font-size:1.05rem;font-weight:600;margin:0}p{margin:0;font-size:.9rem;color:var(--muted)}.request-id{font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;font-size:.75rem;color:var(--muted);word-break:break-all}code{font-family:inherit}.bar{width:9rem;height:3px;margin-top:.6rem;border-radius:3px;background:var(--track);overflow:hidden}.bar::after{content:"";display:block;width:40%;height:100%;border-radius:3px;background:var(--fg);animation:slide 1.1s ease-in-out infinite}@keyframes slide{from{transform:translateX(-100%)}to{transform:translateX(250%)}}@media (prefers-reduced-motion:reduce){.bar::after{animation:none;width:100%;opacity:.35}}#progress:empty{display:none}footer{position:fixed;bottom:1.25rem;left:0;right:0;text-align:center;font-size:.75rem}footer a{color:var(--muted);text-decoration:none}footer a:hover{text-decoration:underline}
        """;

    private const string Logo = "<svg viewBox=\"0 0 32 32\" role=\"img\" aria-label=\"Paduraksa\"><path fill=\"currentColor\" d=\"M4 27V19h2v-5h2V9h2V5h4v22zM28 27V19h-2v-5h-2V9h-2V5h-4v22z\"/><rect fill=\"currentColor\" opacity=\"0.45\" x=\"2\" y=\"28\" width=\"28\" height=\"2\" rx=\"1\"/></svg>";

    private static readonly HtmlEncoder Encoder = HtmlEncoder.Default;

    private readonly byte[][] _segments;
    private readonly PrecomputedHeader[] _headers;

    private HtmlPageTemplate(byte[][] segments, int slotCount, string contentSecurityPolicy)
    {
        _segments = segments;
        SlotCount = slotCount;
        ContentSecurityPolicy = contentSecurityPolicy;
        _headers =
        [
            new("Content-Security-Policy", new StringValues(contentSecurityPolicy)),
            new("Referrer-Policy", new StringValues("no-referrer")),
            new("X-Content-Type-Options", new StringValues("nosniff")),
        ];
    }

    /// <summary>How many values the page expects; each <c>Write</c> overload checks it.</summary>
    public int SlotCount { get; }

    /// <summary>The page's CSP, for a test that recomputes the hashes from what the page really contains.</summary>
    public string ContentSecurityPolicy { get; }

    /// <summary>The inline style exactly as the page inlines it.</summary>
    public static string StyleText => BrandStyle;

    /// <summary>The headers the page carries: the CSP, no referrer and no MIME sniffing.</summary>
    public PrecomputedHeader[] SecurityHeaders => _headers;

    /// <summary>
    /// Builds the layout around <paramref name="body"/>, which is trusted markup with one <c>{name}</c> placeholder per
    /// entry of <paramref name="slots"/>. <paramref name="extraCsp"/> appends directives the page needs beyond the
    /// defaults (the proof-of-work page's worker source).
    /// </summary>
    public static HtmlPageTemplate Create(
        string title,
        string body,
        string? script = null,
        string? extraCsp = null,
        bool allowForm = false,
        params string[] slots)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(slots);

        // The title is the one caller-supplied static string, so it is encoded once here rather than trusted.
        var page = string.Concat(
            "<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">",
            "<title>",
            Encoder.Encode(title),
            "</title><style>",
            BrandStyle,
            "</style></head><body><main>",
            Logo,
            body,
            script is null ? string.Empty : string.Concat("<script>", script, "</script>"),
            "</main><footer><a href=\"https://github.com/hizkifw/paduraksa\">Protected by Paduraksa</a></footer></body></html>\n");

        var segments = new byte[slots.Length + 1][];
        var position = 0;
        for (var i = 0; i < slots.Length; i++)
        {
            var token = string.Concat("{", slots[i], "}");
            var at = page.IndexOf(token, position, StringComparison.Ordinal);
            if (at < 0)
            {
                throw new ArgumentException($"The body has no slot \"{slots[i]}\".", nameof(slots));
            }

            if (page.IndexOf(token, at + token.Length, StringComparison.Ordinal) >= 0)
            {
                throw new ArgumentException($"The body has more than one slot \"{slots[i]}\".", nameof(slots));
            }

            segments[i] = Encoding.UTF8.GetBytes(page[position..at]);
            position = at + token.Length;
        }

        segments[^1] = Encoding.UTF8.GetBytes(page[position..]);

        return new HtmlPageTemplate(segments, slots.Length, BuildContentSecurityPolicy(script, extraCsp, allowForm));
    }

    /// <summary>Writes a one-slot page.</summary>
    public void Write(IBufferWriter<byte> writer, ReadOnlySpan<char> slot0)
    {
        ArgumentNullException.ThrowIfNull(writer);
        RequireSlots(1);
        WriteSegment(writer, _segments[0]);
        WriteSlot(writer, slot0);
        WriteSegment(writer, _segments[1]);
    }

    /// <summary>Writes a two-slot page.</summary>
    public void Write(IBufferWriter<byte> writer, ReadOnlySpan<char> slot0, ReadOnlySpan<char> slot1)
    {
        ArgumentNullException.ThrowIfNull(writer);
        RequireSlots(2);
        WriteSegment(writer, _segments[0]);
        WriteSlot(writer, slot0);
        WriteSegment(writer, _segments[1]);
        WriteSlot(writer, slot1);
        WriteSegment(writer, _segments[2]);
    }

    /// <summary>Writes a three-slot page.</summary>
    public void Write(IBufferWriter<byte> writer, ReadOnlySpan<char> slot0, ReadOnlySpan<char> slot1, ReadOnlySpan<char> slot2)
    {
        ArgumentNullException.ThrowIfNull(writer);
        RequireSlots(3);
        WriteSegment(writer, _segments[0]);
        WriteSlot(writer, slot0);
        WriteSegment(writer, _segments[1]);
        WriteSlot(writer, slot1);
        WriteSegment(writer, _segments[2]);
        WriteSlot(writer, slot2);
        WriteSegment(writer, _segments[3]);
    }

    /// <summary>Renders the page into a new array. For a test, or a response that must carry a body it can measure.</summary>
    public byte[] Render(ReadOnlySpan<char> slot0, ReadOnlySpan<char> slot1 = default, ReadOnlySpan<char> slot2 = default)
    {
        var capacity = 64;
        for (var i = 0; i < _segments.Length; i++)
        {
            capacity += _segments[i].Length;
        }

        ArrayBufferWriter<byte> buffer = new(capacity);
        switch (SlotCount)
        {
            case 1:
                Write(buffer, slot0);
                break;
            case 2:
                Write(buffer, slot0, slot1);
                break;
            default:
                Write(buffer, slot0, slot1, slot2);
                break;
        }

        return buffer.WrittenSpan.ToArray();
    }

    private void RequireSlots(int expected)
    {
        if (SlotCount != expected)
        {
            throw new InvalidOperationException($"This page has {SlotCount} slots, not {expected}.");
        }
    }

    private static void WriteSegment(IBufferWriter<byte> writer, ReadOnlySpan<byte> segment)
    {
        if (segment.IsEmpty)
        {
            return;
        }

        segment.CopyTo(writer.GetSpan(segment.Length));
        writer.Advance(segment.Length);
    }

    private static void WriteSlot(IBufferWriter<byte> writer, ReadOnlySpan<char> value)
    {
        // The encoder writes characters; its output for an HTML slot is always ASCII (anything else becomes an
        // entity), so the encoded chars narrow to bytes without a second buffer. 256 chars holds several characters'
        // escapes, so Encode always makes progress and a long return target is written in bounded steps.
        Span<char> encoded = stackalloc char[256];
        while (!value.IsEmpty)
        {
            var status = Encoder.Encode(value, encoded, out var consumed, out var written, isFinalBlock: true);
            var writtenBytes = Encoding.ASCII.GetBytes(encoded[..written], writer.GetSpan(written));
            writer.Advance(writtenBytes);
            value = value[consumed..];
            if (status == OperationStatus.Done)
            {
                return;
            }

            if (status != OperationStatus.DestinationTooSmall || (consumed == 0 && written == 0))
            {
                throw new InvalidOperationException("The HTML encoder did not make progress.");
            }
        }
    }

    private static string BuildContentSecurityPolicy(string? script, string? extraCsp, bool allowForm)
    {
        StringBuilder csp = new("default-src 'none'; ");
        if (script is not null)
        {
            csp.Append("script-src '").Append(Hash(script)).Append("'; ");
        }

        csp.Append("style-src '").Append(Hash(BrandStyle)).Append('\'');
        if (!string.IsNullOrEmpty(extraCsp))
        {
            csp.Append("; ").Append(extraCsp);
        }

        csp.Append("; base-uri 'none'");
        csp.Append(allowForm ? "; form-action 'self'" : "; form-action 'none'");
        csp.Append("; frame-ancestors 'none'");
        return csp.ToString();
    }

    private static string Hash(string content) => "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
