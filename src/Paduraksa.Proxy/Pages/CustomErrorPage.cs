using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;

using Microsoft.Extensions.Primitives;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;

namespace Paduraksa.Proxy.Pages;

/// <summary>
/// An error page an operator wrote (or the built-in maintenance page), compiled once per snapshot: the text is split around its
/// variables into byte segments, the literal parts encoded (a message) or kept as written (a document), and the status and its name
/// folded into the bytes because a page belongs to one status. Writing one copies the segments into the response pipe and
/// HTML-encodes <c>{requestId}</c>, <c>{host}</c> and <c>{cfRay}</c> straight into it, so a refusal allocates nothing for it.
/// </summary>
/// <remarks>
/// <para><b>Message pages</b> go into the same layout as the built-in pages (<see cref="HtmlPageTemplate"/>) and keep its strict CSP: the
/// brand style's hash and nothing else. <b>Documents</b> are the operator's: a CSP that allows their inline style, <c>data:</c> images and
/// fonts, and nothing that runs or loads from elsewhere (<c>default-src 'none'</c>), plus the framing, referrer and sniffing headers every
/// page carries. Scripts stay out of both: an error page is shown to anyone who can make the host fail, and an operator's page that needs
/// to run code can be a real page of the upstream.</para>
/// <para><b>Hostile values.</b> The request ID comes from a trusted proxy's header, the host from the <c>Host</c> header and the ray from
/// Cloudflare's, so each is the client's to choose: they are written through <see cref="HtmlEncoder"/>, which also turns anything beyond
/// ASCII into entities, and the longer two are cut at <see cref="MaxHostLength"/> and <see cref="MaxRayLength"/> characters.</para>
/// </remarks>
internal sealed class CustomErrorPage
{
    /// <summary>The longest <c>{host}</c> written: a DNS name is at most 253 characters.</summary>
    public const int MaxHostLength = 253;

    /// <summary>The longest <c>{cfRay}</c> written; Cloudflare's are 16 hex digits and a colo code.</summary>
    public const int MaxRayLength = 64;

    private static readonly HtmlEncoder Encoder = HtmlEncoder.Default;

    private static readonly StringValues DocumentPolicy = new(
        "default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'");

    // The headers the built-in pages carry: the message pages' CSP is the brand style's hash, the same for every one of them.
    private static readonly PrecomputedHeader[] BrandHeaders = HtmlPageTemplate.Create("x", "<p>x</p>").SecurityHeaders;

    private readonly byte[][] _segments;
    private readonly ErrorPageVariable[] _variables;

    private CustomErrorPage(byte[][] segments, ErrorPageVariable[] variables, PrecomputedHeader[] headers, ErrorPageMode mode, ErrorPageSource source, int status)
    {
        _segments = segments;
        _variables = variables;
        SecurityHeaders = headers;
        Mode = mode;
        Source = source;
        Status = status;
    }

    public ErrorPageMode Mode { get; }

    public ErrorPageSource Source { get; }

    /// <summary>The status the page was compiled for: the one in its bytes.</summary>
    public int Status { get; }

    /// <summary>The CSP, <c>Referrer-Policy</c> and <c>X-Content-Type-Options</c> this kind of page carries.</summary>
    public PrecomputedHeader[] SecurityHeaders { get; }

    /// <summary>The page the built-in set serves when a host has none of its own: a message about maintenance, for <paramref name="status"/>.</summary>
    public static CustomErrorPage BuiltInMaintenance(int status) => Compile(
        new ErrorPageSpec { Heading = "Down for maintenance", Text = "This site is being worked on. Try again in a little while." },
        status);

    /// <summary>Compiles <paramref name="spec"/> for <paramref name="status"/>. The validator has judged the text; a spec that doesn't hold up (a message without a heading) still gets a page, from what it has.</summary>
    public static CustomErrorPage Compile(ErrorPageSpec spec, int status)
    {
        ArgumentNullException.ThrowIfNull(spec);
        Builder builder = new(status);
        if (spec.Mode == ErrorPageMode.Html)
        {
            builder.Document(spec.Html ?? string.Empty);
            return builder.Build(spec, status, [new PrecomputedHeader("Content-Security-Policy", DocumentPolicy), .. BrandHeaders.Where(static h => h.Name != "Content-Security-Policy")]);
        }

        var heading = spec.Heading is { Length: > 0 } text ? text : $"{{status}} {{statusText}}";
        builder.Raw(HtmlPageTemplate.DocumentStart);
        builder.Text(heading);
        builder.Raw(HtmlPageTemplate.TitleEnd);
        builder.Raw("<h1>");
        builder.Text(heading);
        builder.Raw("</h1>");
        if (!string.IsNullOrEmpty(spec.Text))
        {
            builder.Raw("<p>");
            builder.Text(spec.Text);
            builder.Raw("</p>");
        }

        builder.Raw("<p class=\"request-id\">Request ID: <code>");
        builder.Variable(ErrorPageVariable.RequestId);
        builder.Raw("</code></p>");
        builder.Raw(HtmlPageTemplate.DocumentEnd);
        return builder.Build(spec, status, BrandHeaders);
    }

    /// <summary>Writes the page. <paramref name="cfRay"/> is empty unless Cloudflare's headers are trusted for the request.</summary>
    public void Write(IBufferWriter<byte> writer, ReadOnlySpan<char> requestId, ReadOnlySpan<char> host, ReadOnlySpan<char> cfRay)
    {
        ArgumentNullException.ThrowIfNull(writer);
        for (var i = 0; i < _variables.Length; i++)
        {
            HtmlPageTemplate.WriteSegment(writer, _segments[i]);
            switch (_variables[i])
            {
                case ErrorPageVariable.RequestId:
                    HtmlPageTemplate.WriteSlot(writer, requestId);
                    break;
                case ErrorPageVariable.Host:
                    HtmlPageTemplate.WriteSlot(writer, host.Length > MaxHostLength ? host[..MaxHostLength] : host);
                    break;
                default:
                    HtmlPageTemplate.WriteSlot(writer, cfRay.Length > MaxRayLength ? cfRay[..MaxRayLength] : cfRay);
                    break;
            }
        }

        HtmlPageTemplate.WriteSegment(writer, _segments[^1]);
    }

    /// <summary>The page as bytes, for a preview and for the tests that compare it with what a request is served.</summary>
    public byte[] Render(string requestId, string host, string cfRay)
    {
        ArrayBufferWriter<byte> buffer = new(1024);
        Write(buffer, requestId, host, cfRay);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Collects the segments and the runtime variables between them. The status and its name are folded into the literals here, so
    /// what is left at write time is the three values that belong to the request.
    /// </summary>
    private sealed class Builder(int status)
    {
        private readonly List<byte[]> _segments = [];
        private readonly List<ErrorPageVariable> _variables = [];
        private readonly StringBuilder _pending = new();
        private readonly string _statusText = ErrorPageStatuses.StatusText(status);
        private readonly string _status = status.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Trusted markup: the layout.</summary>
        public void Raw(string markup) => _pending.Append(markup);

        /// <summary>The operator's own document: its literal text stays as written, its variables are filled in.</summary>
        public void Document(string html) => Append(html, encodeLiteral: false);

        /// <summary>The operator's heading or sentence: plain text, so its literal parts are encoded.</summary>
        public void Text(string text) => Append(text, encodeLiteral: true);

        public void Variable(ErrorPageVariable variable)
        {
            _segments.Add(Encoding.UTF8.GetBytes(_pending.ToString()));
            _pending.Clear();
            _variables.Add(variable);
        }

        public CustomErrorPage Build(ErrorPageSpec spec, int pageStatus, PrecomputedHeader[] headers)
        {
            _segments.Add(Encoding.UTF8.GetBytes(_pending.ToString()));
            return new CustomErrorPage([.. _segments], [.. _variables], headers, spec.Mode, spec.Source, pageStatus);
        }

        private void Append(string text, bool encodeLiteral)
        {
            foreach (var part in ErrorPageText.Parse(text, out _))
            {
                if (part.Text is { } literal)
                {
                    _pending.Append(encodeLiteral ? Encoder.Encode(literal) : literal);
                    continue;
                }

                switch (part.Variable!.Value)
                {
                    case ErrorPageVariable.Status:
                        _pending.Append(_status);
                        break;
                    case ErrorPageVariable.StatusText:
                        _pending.Append(Encoder.Encode(_statusText));
                        break;
                    default:
                        Variable(part.Variable.Value);
                        break;
                }
            }
        }
    }
}

/// <summary>
/// The pages one host (or the global defaults, for an unknown host) answers with in place of the built-in ones, by
/// <see cref="ErrorPageStatuses"/> key; a key with none is served from the built-in page. Compiled once per snapshot.
/// </summary>
internal sealed class CompiledErrorPages
{
    /// <summary>No page of its own and no replacement.</summary>
    public static CompiledErrorPages None { get; } = new(new CustomErrorPage?[ErrorPageStatuses.Keys.Count], UpstreamErrorReplacement.Off);

    private readonly CustomErrorPage?[] _pages;

    private CompiledErrorPages(CustomErrorPage?[] pages, UpstreamErrorReplacement replaceUpstream)
    {
        _pages = pages;
        ReplaceUpstream = replaceUpstream;
    }

    /// <summary>When an upstream's own error responses give way to these pages.</summary>
    public UpstreamErrorReplacement ReplaceUpstream { get; }

    /// <summary>Compiles the statuses' pages; the maintenance page is compiled separately, for the status the host answers it with.</summary>
    public static CompiledErrorPages Compile(HostErrorPagesSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var pages = new CustomErrorPage?[ErrorPageStatuses.Keys.Count];
        foreach (var status in ErrorPageStatuses.Statuses)
        {
            if (spec.Pages.TryGetValue(status, out var page))
            {
                pages[ErrorPageStatuses.IndexOf(status)] = CustomErrorPage.Compile(page, status);
            }
        }

        return new CompiledErrorPages(pages, spec.ReplaceUpstream);
    }

    /// <summary>The page the host wrote for <paramref name="status"/>, or null for the built-in one.</summary>
    public CustomErrorPage? Find(int status)
    {
        var index = ErrorPageStatuses.IndexOf(status);
        return index >= 0 && status != ErrorPageStatuses.MaintenanceKey ? _pages[index] : null;
    }
}
