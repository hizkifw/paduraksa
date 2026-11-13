using System.Buffers;

using Paduraksa.Core.Model;

namespace Paduraksa.Proxy.Snapshot;

/// <summary>
/// A route's compiled path rewrite (docs/design/hot-path.md "Path rewrite"): strip the route's path off the front of the
/// path the client sent, put a fixed prefix on, or both. It works on the raw path, so nothing is decoded or re-encoded, and
/// the query is never touched. Public (the cold-path members) so the validator checks a prefix with the same rules.
/// </summary>
public sealed class PathRewrite
{
    /// <summary>Longest prefix a route may add.</summary>
    public const int MaxAddPrefixLength = 256;

    // RFC 3986 pchar and '/', '%' for an escape (checked on its own).
    private static readonly SearchValues<char> PathChars =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~!$&'()*+,;=:@/%");

    /// <param name="routePath">The route's canonical path, the prefix a strip takes off.</param>
    /// <param name="kind">How the route matches.</param>
    /// <param name="stripPrefix">Take <paramref name="routePath"/> off the front.</param>
    /// <param name="addPrefix">Put in front; null or empty for none. Already checked.</param>
    /// <param name="rewriteLocation">Give an upstream's <c>Location</c> the client's prefix back (a stripping route only).</param>
    internal PathRewrite(string routePath, RouteMatchKind kind, bool stripPrefix, string? addPrefix, bool rewriteLocation = true)
    {
        RoutePrefix = routePath.Length > 1 ? routePath.TrimEnd('/') : routePath;
        EncodedPrefix = Encode(RoutePrefix);
        Kind = kind;
        StripPrefix = stripPrefix;
        AddPrefix = string.IsNullOrEmpty(addPrefix) ? string.Empty : addPrefix;
        RewriteLocation = stripPrefix && rewriteLocation;
        ForwardedPrefix = stripPrefix ? EncodedPrefix : null;
    }

    /// <summary>The route's path without a trailing <c>/</c>, decoded as the config writes it (<c>/café</c>).</summary>
    public string RoutePrefix { get; }

    /// <summary>
    /// <see cref="RoutePrefix"/> as a client sends it, percent-encoded as UTF-8 wherever a path needs it (<c>/caf%C3%A9</c>,
    /// <c>/my%20app</c>): what a strip compares the raw path with, what <c>X-Forwarded-Prefix</c> carries (ASCII, so a header can),
    /// and what a <c>Location</c> gets back. The same string for an ASCII path of plain characters.
    /// </summary>
    public string EncodedPrefix { get; }

    /// <summary>Whether an upstream's <c>Location</c> gets the client's prefix back: on for a stripping route unless its config
    /// turns it off, never for an add-only one.</summary>
    public bool RewriteLocation { get; }

    public RouteMatchKind Kind { get; }

    public bool StripPrefix { get; }

    /// <summary>What goes in front; empty for none.</summary>
    public string AddPrefix { get; }

    /// <summary>The <c>X-Forwarded-Prefix</c> the upstream is sent: the stripped prefix, or null when nothing is stripped.</summary>
    public string? ForwardedPrefix { get; }

    /// <summary>Why <paramref name="prefix"/> can't be added, or null when it can.</summary>
    public static string? AddPrefixProblem(string? prefix)
    {
        if (string.IsNullOrEmpty(prefix) || prefix[0] != '/')
        {
            return "a prefix starts with '/'";
        }

        if (prefix.Length > MaxAddPrefixLength)
        {
            return $"a prefix can be at most {MaxAddPrefixLength} characters";
        }

        if (prefix.Length == 1 || prefix[^1] == '/')
        {
            return "a prefix doesn't end with '/' (the forwarded path brings its own)";
        }

        if (prefix.AsSpan().ContainsAnyExcept(PathChars))
        {
            return "a prefix takes letters, digits, -._~!$&'()*+,;=:@/ and percent escapes only";
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (prefix[i] == '%' && (i + 2 >= prefix.Length || !char.IsAsciiHexDigit(prefix[i + 1]) || !char.IsAsciiHexDigit(prefix[i + 2])))
            {
                return "a '%' in a prefix starts a two-digit escape";
            }
        }

        foreach (var segment in prefix[1..].Split('/'))
        {
            if (segment.Length == 0)
            {
                return "a prefix has no empty segments";
            }
        }

        // The same rules a forwarded path is held to, so the prefix itself can't step out of anything an upstream decodes.
        if (HasDotSegment(prefix) || HasLooseEscape(prefix))
        {
            return "a prefix has no '.' or '..' segment in any spelling, and no %25, %c0, %c1, %u or control-character escape";
        }

        return null;
    }

    // RFC 3986 unreserved and sub-delims, ':', '@' and '/' go as they are; everything else is UTF-8, percent-encoded upper case.
    private static readonly SearchValues<char> Plain =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~!$&'()*+,;=:@/");

    private static string Encode(string path)
    {
        if (!path.AsSpan().ContainsAnyExcept(Plain))
        {
            return path;
        }

        System.Text.StringBuilder encoded = new(path.Length * 3);
        Span<byte> bytes = stackalloc byte[4];
        for (var i = 0; i < path.Length; i++)
        {
            var c = path[i];
            if (Plain.Contains(c))
            {
                encoded.Append(c);
                continue;
            }

            var length = char.IsHighSurrogate(c) && i + 1 < path.Length
                ? System.Text.Encoding.UTF8.GetBytes(path.AsSpan(i++, 2), bytes)
                : System.Text.Encoding.UTF8.GetBytes(path.AsSpan(i, 1), bytes);
            foreach (var b in bytes[..length])
            {
                encoded.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return encoded.ToString();
    }

    /// <summary>
    /// Where the stripped part of <paramref name="path"/> (a raw path, before any <c>?</c>) ends (0 without a strip), or -1 when
    /// the path can't be rewritten without a guess: the client spelled the route's prefix another way, or what is forwarded has a
    /// dot segment in any spelling (<see cref="HasDotSegment"/>). Ignores ASCII case, as matching does. 0 B.
    /// </summary>
    public int StripEnd(ReadOnlySpan<char> path)
    {
        var end = StripPrefix ? PrefixEnd(path) : 0;
        return end >= 0 && !HasDotSegment(path[end..]) && !HasLooseEscape(path[end..]) ? end : -1;
    }

    internal int PrefixEnd(ReadOnlySpan<char> path)
    {
        var end = PrefixEnd(path, EncodedPrefix);

        // The decoded spelling too, where it differs: no HTTP client sends it raw, but the simulator's probe may be typed that way.
        return end < 0 && !ReferenceEquals(EncodedPrefix, RoutePrefix) ? PrefixEnd(path, RoutePrefix) : end;
    }

    private int PrefixEnd(ReadOnlySpan<char> path, ReadOnlySpan<char> prefix)
    {
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        if (path.Length == prefix.Length)
        {
            return prefix.Length;
        }

        // An exact route takes the path itself (and the trailing '/' its own path may have); a prefix route a segment boundary.
        var rest = path[prefix.Length..];
        return rest[0] == '/' && (Kind == RouteMatchKind.Prefix || rest.Length == 1) ? prefix.Length : -1;
    }

    /// <summary>
    /// Whether <paramref name="path"/> has a segment an upstream could resolve as <c>.</c> or <c>..</c>. Kestrel resolves literal dot
    /// segments before matching, but the raw path is what is forwarded, and the matching view never decodes <c>%2e</c>: with a prefix
    /// taken off or put on, <c>/app/%2e%2e/admin</c> would reach the upstream as <c>/api/%2e%2e/admin</c>, which it decodes to
    /// <c>/admin</c>, outside the subtree the route exposes. So a segment (between <c>/</c>, <c>\</c>, <c>%2F</c> or <c>%5C</c>)
    /// whose <c>;</c> parameters are dropped and whose <c>%2e</c> and <c>%20</c> are decoded, in any case, and which is then only
    /// dots and spaces (<c>.</c>, <c>..</c>, <c>%2e.</c>, <c>..;x</c>, <c>... </c>) makes the path unrewritable. An encoded slash in
    /// an ordinary segment (<c>group%2Fproject</c>) is fine. 0 B.
    /// </summary>
    public static bool HasDotSegment(ReadOnlySpan<char> path)
    {
        var dots = 0;
        var other = false;
        var inParameters = false;
        for (var i = 0; i <= path.Length; i++)
        {
            var separator = i == path.Length || path[i] is '/' or '\\' || IsEscape(path, i, '2', 'f') || IsEscape(path, i, '5', 'c');
            if (separator)
            {
                if (dots > 0 && !other)
                {
                    return true;
                }

                dots = 0;
                other = false;
                inParameters = false;
                if (i < path.Length && path[i] == '%')
                {
                    i += 2;
                }

                continue;
            }

            if (inParameters)
            {
                continue;
            }

            var c = path[i];
            if (c == ';')
            {
                inParameters = true;
            }
            else if (c == '.' || IsEscape(path, i, '2', 'e'))
            {
                dots++;
                i += c == '%' ? 2 : 0;
            }
            else if (c == ' ' || IsEscape(path, i, '2', '0'))
            {
                i += c == '%' ? 2 : 0;
            }
            else
            {
                other = true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="path"/> has an escape that an upstream decoding loosely could turn into something the gate never saw:
    /// <c>%25</c> (a double encoding, <c>%252e</c>), <c>%c0</c>/<c>%c1</c> (an overlong UTF-8 lead, <c>%c0%ae</c> for <c>.</c>),
    /// IIS's <c>%u</c> form, and <c>%00</c>-<c>%1f</c> and <c>%7f</c> (control characters). Any case. 0 B.
    /// </summary>
    public static bool HasLooseEscape(ReadOnlySpan<char> path)
    {
        for (var i = 0; i + 1 < path.Length; i++)
        {
            if (path[i] != '%')
            {
                continue;
            }

            var high = (char)(path[i + 1] | 0x20);
            if (high == 'u' || high is '0' or '1')
            {
                return true;
            }

            var low = i + 2 < path.Length ? (char)(path[i + 2] | 0x20) : '\0';
            if ((high == '2' && low == '5') || (high == 'c' && low is '0' or '1') || (high == '7' && low == 'f'))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEscape(ReadOnlySpan<char> path, int i, char high, char low) =>
        path[i] == '%' && i + 2 < path.Length && path[i + 1] == high && (path[i + 2] | 0x20) == low;

    /// <summary>The forwarded path as one string, for the simulator and the tests (allocates).</summary>
    public string? ForwardedPath(string rawPath)
    {
        var end = StripEnd(rawPath);
        if (end < 0)
        {
            return null;
        }

        return end == rawPath.Length && AddPrefix.Length == 0 ? "/" : string.Concat(AddPrefix, rawPath.AsSpan(end));
    }

    /// <summary>
    /// The <c>Location</c> the client should see for an upstream's <paramref name="location"/>, or null to leave it as sent. Only a
    /// stripping route with <see cref="RewriteLocation"/> maps it; then a path-absolute <c>Location</c>, or an absolute one on the
    /// request's own <paramref name="host"/> or on <paramref name="upstreamAuthority"/> (the Host or address the upstream was sent):
    /// <list type="bullet">
    /// <item>one that already starts with the client's prefix (a prefix-aware app, which <c>X-Forwarded-Prefix</c> invites) is left
    /// alone;</item>
    /// <item>one that starts with what the route sends upstream (the added prefix, or the root) gets the client's prefix in its
    /// place;</item>
    /// <item>one on the upstream's own address gets the client's origin (<paramref name="clientScheme"/> and <paramref name="host"/>)
    /// whatever its path, so an internal address never reaches the client.</item>
    /// </list>
    /// A protocol-relative one (<c>//</c>, or <c>/\</c>, which a browser reads the same), another host or another port is left alone.
    /// Only redirects pay, with one string.
    /// </summary>
    public string? ClientLocation(string location, string? host, string? upstreamAuthority = null, string clientScheme = "https")
    {
        if (!RewriteLocation)
        {
            return null;
        }

        var span = location.AsSpan();
        var authorityEnd = 0;
        var upstreamOrigin = false;
        if (!span.StartsWith('/'))
        {
            var schemeEnd = span.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 8 : span.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? 7 : -1;
            if (schemeEnd < 0)
            {
                return null;
            }

            var slash = span[schemeEnd..].IndexOfAny('/', '?', '#');
            var authority = slash < 0 ? span[schemeEnd..] : span.Slice(schemeEnd, slash);
            if (!string.IsNullOrEmpty(upstreamAuthority) && authority.Equals(upstreamAuthority, StringComparison.OrdinalIgnoreCase)
                && !authority.Equals(host, StringComparison.OrdinalIgnoreCase))
            {
                upstreamOrigin = true;
            }
            else if (string.IsNullOrEmpty(host) || !authority.Equals(host, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            authorityEnd = slash < 0 ? span.Length : schemeEnd + slash;
        }
        else if (span.StartsWith("//") || span.StartsWith("/\\"))
        {
            return null;
        }

        var origin = upstreamOrigin ? string.Concat(clientScheme, "://", host) : span[..authorityEnd].ToString();
        var path = span[authorityEnd..];
        var client = EncodedPrefix.Length == 1 ? ReadOnlySpan<char>.Empty : EncodedPrefix.AsSpan();
        if (AtBoundary(path, client, StringComparison.OrdinalIgnoreCase, out _))
        {
            return upstreamOrigin ? string.Concat(origin, path) : null;
        }

        if (!AtBoundary(path, AddPrefix, StringComparison.Ordinal, out var rest))
        {
            return upstreamOrigin ? string.Concat(origin, path.IsEmpty ? "/" : path) : null;
        }

        if (client.IsEmpty && (rest.IsEmpty || rest[0] != '/'))
        {
            return string.Concat(origin, "/", rest);
        }

        return string.Concat(origin, client, rest);
    }

    /// <summary>Whether <paramref name="path"/> starts with <paramref name="prefix"/> followed by the end, <c>/</c>, <c>?</c> or <c>#</c>; an empty
    /// prefix takes any path.</summary>
    private static bool AtBoundary(ReadOnlySpan<char> path, ReadOnlySpan<char> prefix, StringComparison comparison, out ReadOnlySpan<char> rest)
    {
        rest = path;
        if (prefix.IsEmpty)
        {
            return true;
        }

        if (!path.StartsWith(prefix, comparison))
        {
            return false;
        }

        rest = path[prefix.Length..];
        return rest.IsEmpty || rest[0] is '/' or '?' or '#';
    }
}
