using System.Buffers;

namespace Paduraksa.Proxy.Honeypot;

/// <summary>
/// One host's trap site (docs/design/waf.md "Honeypot"). Every page is derived from the site key and the URL, so the site
/// keeps no state and the same URL always renders the same bytes for the same visitor.
/// </summary>
public interface IHoneypotSite
{
    /// <summary>The trap prefix, canonical, starting and ending with <c>/</c>.</summary>
    string Prefix { get; }

    /// <summary>
    /// What a path inside <see cref="Prefix"/> names. A path with a token that fails to decode or verify resolves to
    /// <see cref="HoneypotPageKind.NotFound"/>, never throws.
    /// </summary>
    HoneypotTarget Resolve(ReadOnlySpan<char> canonicalPath);

    /// <summary>The landing page a rule's honeypot action answers for a path outside the prefix.</summary>
    HoneypotTarget Landing(ReadOnlySpan<char> canonicalPath);

    /// <summary>Writes the body for <paramref name="target"/>. <paramref name="canary"/> is the visitor's phrase, woven into post text.</summary>
    HoneypotRender Render(in HoneypotTarget target, ReadOnlySpan<char> canary, IBufferWriter<byte> output);

    /// <summary>The visitor's two-word canary phrase, derived from the site key, the client IP and the user agent.</summary>
    string Canary(ReadOnlySpan<byte> clientIp, ReadOnlySpan<char> userAgent);
}

/// <summary>What a trap URL names.</summary>
/// <param name="Kind">The page kind.</param>
/// <param name="Via">The kind of link the URL was published as; <see cref="HoneypotLinkKind.Direct"/> for a URL no link carries (the index, a guessed URL, a landing).</param>
/// <param name="PageId">The page's ID, the seed of its content.</param>
/// <param name="ParentId">The page whose link this URL is; 0 when there is none.</param>
/// <param name="NeedsPow">The page is behind the trap's proof-of-work challenge.</param>
public readonly record struct HoneypotTarget(
    HoneypotPageKind Kind,
    HoneypotLinkKind Via,
    ulong PageId,
    ulong ParentId,
    bool NeedsPow);

/// <summary>How to answer a rendered target.</summary>
/// <param name="Status">200, or 404 for <see cref="HoneypotPageKind.NotFound"/>.</param>
/// <param name="ContentType">Including the charset where it applies.</param>
/// <param name="LastModified">The page's derived date, for <c>Last-Modified</c>; null when the kind has none.</param>
public readonly record struct HoneypotRender(int Status, string ContentType, DateTimeOffset? LastModified);

public enum HoneypotPageKind : byte
{
    Index,
    IndexPage,
    Post,
    Category,
    Tag,
    Author,
    Sitemap,
    Feed,
    Image,
    Beacon,
    Landing,
    NotFound,
}

public enum HoneypotLinkKind : byte
{
    Direct,
    Normal,
    NoFollow,
    Ugc,
    Sponsored,
    Hidden,
    Comment,
    Script,
    Beacon,
    Sitemap,
    Feed,
    NoIndexPage,
    Image,
}
