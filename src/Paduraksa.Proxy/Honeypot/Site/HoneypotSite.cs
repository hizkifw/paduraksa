using System.Buffers;
using System.Net;
using System.Text;

namespace Paduraksa.Proxy.Honeypot;

/// <summary>
/// The trap blog (docs/design/waf.md "Honeypot"), as a pure function of the site key and the URL: it keeps no state, reads no clock and holds
/// no settings beyond its options, so the same URL renders the same bytes for the same visitor and a host can rebuild it at will.
/// </summary>
public sealed class HoneypotSite : IHoneypotSite
{
    /// <summary>Resolve refuses a longer path at once. The longest URL the site makes is about a hundred and fifty characters.</summary>
    private const int MaxPathLength = 256;

    internal const int MaxIndexPage = 99_999;
    private const string Base32 = "abcdefghijklmnopqrstuvwxyz234567";

    private readonly byte[] _key;
    private readonly int _powShare;
    private readonly ulong _powSalt;
    private readonly ulong _noIndexSalt;
    private readonly ulong _tokenSalt;

    public HoneypotSite(ReadOnlySpan<byte> siteKey, HoneypotSiteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (siteKey.Length != SiteKeys.KeyLength)
        {
            throw new ArgumentException($"The site key is {SiteKeys.KeyLength} bytes.", nameof(siteKey));
        }

        if (!IsCanonicalPrefix(options.Prefix))
        {
            throw new ArgumentException("The prefix starts and ends with '/' and holds only letters, digits and . _ ~ - /.", nameof(options));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(options.PowShare);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.PowShare, 100);

        _key = siteKey.ToArray();
        _powShare = options.PowShare;
        Prefix = options.Prefix;
        PrefixBytes = Encoding.ASCII.GetBytes(options.Prefix);

        var name = string.IsNullOrWhiteSpace(options.SiteName) ? DeriveSiteName(siteKey) : options.SiteName.Trim();
        NameBytes = Encoding.UTF8.GetBytes(WebUtility.HtmlEncode(name));

        var salts = SiteKeys.DomainRandom(_key, SiteKeys.SaltDomain);
        _powSalt = salts.NextUInt64();
        _noIndexSalt = salts.NextUInt64();
        _tokenSalt = salts.NextUInt64();
        var accent = salts.Below(360);

        var cast = SiteKeys.DomainRandom(_key, SiteKeys.CastDomain);
        CategoryNames = Pick(Lexicon.Categories, 8, ref cast);
        CategorySlugs = Lowered(CategoryNames);
        TagNames = Pick(Lexicon.Nouns, 24, ref cast);
        TagSlugs = TagNames;
        AuthorNames = BuildAuthors(4, ref cast);
        AuthorSlugs = SlugsOf(AuthorNames);
        Theme = Theming.Build(accent);
    }

    public string Prefix { get; }

    internal ReadOnlySpan<byte> Key => _key;

    internal ulong TokenSalt => _tokenSalt;

    internal byte[] PrefixBytes { get; }

    /// <summary>The blog's title, escaped for HTML and XML text and attributes.</summary>
    internal byte[] NameBytes { get; }

    internal byte[][] CategoryNames { get; }

    internal byte[][] CategorySlugs { get; }

    internal byte[][] TagNames { get; }

    internal byte[][] TagSlugs { get; }

    internal byte[][] AuthorNames { get; }

    internal byte[][] AuthorSlugs { get; }

    /// <summary>The <c>&lt;style&gt;</c> element, with this site's accent colour.</summary>
    internal byte[] Theme { get; }

    /// <summary>A prefix such as <c>/archive-k7f3/</c>: a word and four base32 characters of the key.</summary>
    public static string DerivePrefix(ReadOnlySpan<byte> siteKey)
    {
        var random = SiteKeys.DomainRandom(siteKey, SiteKeys.PrefixDomain);
        var word = Lexicon.PrefixWords[random.Below(Lexicon.PrefixWords.Length)];
        Span<char> suffix = stackalloc char[4];
        for (var i = 0; i < suffix.Length; i++)
        {
            suffix[i] = Base32[random.Below(32)];
        }

        return string.Concat("/", word, "-", suffix.ToString(), "/");
    }

    /// <summary>A believable blog title, such as "Notes from the Orchard".</summary>
    public static string DeriveSiteName(ReadOnlySpan<byte> siteKey)
    {
        var random = SiteKeys.DomainRandom(siteKey, SiteKeys.NameDomain);
        Span<byte> buffer = stackalloc byte[64];
        var o = new Utf8Out(buffer);
        TextGen.SiteName(ref o, ref random);
        return Encoding.ASCII.GetString(buffer[..o.Length]);
    }

    public HoneypotTarget Resolve(ReadOnlySpan<char> canonicalPath)
    {
        if (!canonicalPath.StartsWith(Prefix, StringComparison.Ordinal) || canonicalPath.Length - Prefix.Length > MaxPathLength)
        {
            return NotFound;
        }

        var rest = canonicalPath[Prefix.Length..];
        if (rest.IsEmpty)
        {
            return new HoneypotTarget(HoneypotPageKind.Index, HoneypotLinkKind.Direct, 0, 0, false);
        }

        if (rest.Equals("sitemap.xml", StringComparison.Ordinal))
        {
            return Direct(HoneypotPageKind.Sitemap, 0);
        }

        if (rest.Equals("feed/", StringComparison.Ordinal))
        {
            return Direct(HoneypotPageKind.Feed, 0);
        }

        if (rest.StartsWith("page/", StringComparison.Ordinal))
        {
            return ResolveIndexPage(rest["page/".Length..]);
        }

        if (rest.StartsWith("category/", StringComparison.Ordinal))
        {
            return ResolveNamed(HoneypotPageKind.Category, "category/", rest, CategoryNames.Length);
        }

        if (rest.StartsWith("tag/", StringComparison.Ordinal))
        {
            return ResolveNamed(HoneypotPageKind.Tag, "tag/", rest, TagNames.Length);
        }

        if (rest.StartsWith("author/", StringComparison.Ordinal))
        {
            return ResolveNamed(HoneypotPageKind.Author, "author/", rest, AuthorNames.Length);
        }

        if (rest.StartsWith("media/", StringComparison.Ordinal) && rest.EndsWith(".svg", StringComparison.Ordinal))
        {
            return ResolveToken(HoneypotPageKind.Image, rest[..^".svg".Length]);
        }

        if (rest.StartsWith("assets/stats-", StringComparison.Ordinal) && rest.EndsWith(".json", StringComparison.Ordinal))
        {
            return ResolveToken(HoneypotPageKind.Beacon, rest[..^".json".Length]);
        }

        // A post: yyyy/mm/<slug>-<token>/. The year and month are covered by the token's tag, like the slug.
        if (rest.Length > 10 && rest[4] == '/' && rest[7] == '/' && rest[^1] == '/' && IsDigits(rest[..4]) && IsDigits(rest.Slice(5, 2)))
        {
            return ResolveToken(HoneypotPageKind.Post, rest[..^1]);
        }

        return NotFound;
    }

    public HoneypotTarget Landing(ReadOnlySpan<char> canonicalPath)
    {
        // FNV-1a over the path, case-blind: a probe for /Wp-Login.php and /wp-login.php meets the same page.
        var hash = 14695981039346656037UL;
        foreach (var c in canonicalPath[..Math.Min(canonicalPath.Length, MaxPathLength * 8)])
        {
            hash = (hash ^ (c is >= 'A' and <= 'Z' ? (ulong)(c + 32) : c)) * 1099511628211UL;
        }

        var local = (uint)(SiteRandom.Mix(hash) & SiteToken.MaxLocal);
        return new HoneypotTarget(HoneypotPageKind.Landing, HoneypotLinkKind.Direct, Pack(HoneypotPageKind.Landing, local), 0, false);
    }

    public HoneypotRender Render(in HoneypotTarget target, ReadOnlySpan<char> canary, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return PageRenderer.Render(this, target, canary, output);
    }

    public string Canary(ReadOnlySpan<byte> clientIp, ReadOnlySpan<char> userAgent)
    {
        // A longer user agent is cut: it bounds the work, and a visitor whose header differs only past 1024 characters is the same visitor to a human.
        userAgent = userAgent[..Math.Min(userAgent.Length, 1024)];
        var input = new byte[1 + Math.Min(clientIp.Length, 16) + 1 + Encoding.UTF8.GetMaxByteCount(userAgent.Length)];
        input[0] = SiteKeys.CanaryDomain;
        var length = 1;
        clientIp[..Math.Min(clientIp.Length, 16)].CopyTo(input.AsSpan(length));
        length += Math.Min(clientIp.Length, 16);
        input[length++] = 0;
        length += Encoding.UTF8.GetBytes(userAgent, input.AsSpan(length));

        Span<byte> mac = stackalloc byte[SiteKeys.MacLength];
        SiteKeys.Mac(_key, input.AsSpan(0, length), mac);
        var random = new SiteRandom(mac);
        Span<byte> words = stackalloc byte[48];
        var o = new Utf8Out(words);
        TextGen.MadeUpWord(ref o, ref random);
        o.Write((byte)' ');
        TextGen.MadeUpWord(ref o, ref random);
        return Encoding.ASCII.GetString(words[..o.Length]);
    }

    internal static ulong Pack(HoneypotPageKind kind, uint local) => ((ulong)kind << 28) | local;

    /// <summary>Whether the page is behind the trap's challenge. Posts, categories, tags and authors can be; entry pages and resources never are.</summary>
    internal bool NeedsPow(HoneypotPageKind kind, ulong packed) =>
        kind is HoneypotPageKind.Post or HoneypotPageKind.Category or HoneypotPageKind.Tag or HoneypotPageKind.Author
        && _powShare > 0
        && SiteRandom.Mix(packed ^ _powSalt) % 100 < (ulong)_powShare;

    /// <summary>
    /// Whether the page carries <c>noindex, nofollow</c>, about one in ten. The entry pages (the index, a landing, a 404) never do: they are
    /// where a crawler is meant to start, and a crawler that stops there sees nothing.
    /// </summary>
    internal bool IsNoIndex(HoneypotPageKind kind, ulong packed) =>
        kind is HoneypotPageKind.Post or HoneypotPageKind.Category or HoneypotPageKind.Tag or HoneypotPageKind.Author or HoneypotPageKind.IndexPage
        && SiteRandom.Mix(packed ^ _noIndexSalt) % 10 == 0;

    private static HoneypotTarget NotFound { get; } = new(HoneypotPageKind.NotFound, HoneypotLinkKind.Direct, Pack(HoneypotPageKind.NotFound, 0), 0, false);

    private static bool IsCanonicalPrefix(string prefix)
    {
        if (prefix.Length < 3 || prefix[0] != '/' || prefix[^1] != '/')
        {
            return false;
        }

        foreach (var c in prefix)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '/' or '.' or '_' or '~' or '-'))
            {
                return false;
            }
        }

        return !prefix.Contains("//", StringComparison.Ordinal);
    }

    private static bool IsDigits(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private HoneypotTarget Direct(HoneypotPageKind kind, uint local) =>
        new(kind, HoneypotLinkKind.Direct, Pack(kind, local), 0, NeedsPow(kind, Pack(kind, local)));

    private HoneypotTarget ResolveIndexPage(ReadOnlySpan<char> afterPage)
    {
        if (afterPage.Length is < 2 or > 6 || afterPage[^1] != '/' || afterPage[0] == '0')
        {
            return NotFound;
        }

        var digits = afterPage[..^1];
        if (!IsDigits(digits) || !uint.TryParse(digits, out var number) || number < 2 || number > MaxIndexPage)
        {
            return NotFound;
        }

        return Direct(HoneypotPageKind.IndexPage, number);
    }

    private HoneypotTarget ResolveNamed(HoneypotPageKind kind, string folder, ReadOnlySpan<char> rest, int count)
    {
        if (rest[^1] != '/')
        {
            return NotFound;
        }

        var target = ResolveToken(kind, rest[..^1]);
        return target.Kind == kind && (target.PageId & SiteToken.MaxLocal) < (ulong)count && rest.Length > folder.Length ? target : NotFound;
    }

    /// <summary>
    /// <paramref name="body"/> is the path after the prefix without its suffix: <c>&lt;text&gt;-&lt;token&gt;</c>. The text, which is
    /// what the tag covers, must be printable ASCII and short.
    /// </summary>
    private HoneypotTarget ResolveToken(HoneypotPageKind kind, ReadOnlySpan<char> body)
    {
        const int TokenWithDash = SiteToken.Length + 1;
        if (body.Length <= TokenWithDash || body.Length - TokenWithDash > SiteToken.MaxTail || body[^TokenWithDash] != '-')
        {
            return NotFound;
        }

        var text = body[..^TokenWithDash];
        Span<byte> tail = stackalloc byte[SiteToken.MaxTail];
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is < '!' or > '~')
            {
                return NotFound;
            }

            tail[i] = (byte)text[i];
        }

        if (!SiteToken.TryDecode(_key, _tokenSalt, kind, body[^SiteToken.Length..], tail[..text.Length], out var via, out var local, out var parentKind, out var parentLocal))
        {
            return NotFound;
        }

        var id = Pack(kind, local);
        return new HoneypotTarget(kind, via, id, Pack(parentKind, parentLocal), NeedsPow(kind, id));
    }

    private static byte[][] Pick(byte[][] pool, int count, ref SiteRandom random)
    {
        // A partial Fisher-Yates over the indexes: the site's categories and tags are a subset of the pool, in an order of its own.
        var indexes = new int[pool.Length];
        for (var i = 0; i < indexes.Length; i++)
        {
            indexes[i] = i;
        }

        var picked = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            var j = i + random.Below(indexes.Length - i);
            (indexes[i], indexes[j]) = (indexes[j], indexes[i]);
            picked[i] = pool[indexes[i]];
        }

        return picked;
    }

    private static byte[][] BuildAuthors(int count, ref SiteRandom random)
    {
        var authors = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            var first = Lexicon.FirstNames[random.Below(Lexicon.FirstNames.Length)];
            var last = Lexicon.LastNames[random.Below(Lexicon.LastNames.Length)];
            authors[i] = [.. first, (byte)' ', .. last];
        }

        return authors;
    }

    private static byte[][] Lowered(byte[][] names)
    {
        var lowered = new byte[names.Length][];
        for (var i = 0; i < names.Length; i++)
        {
            lowered[i] = Slug(names[i]);
        }

        return lowered;
    }

    private static byte[] Slug(byte[] name)
    {
        Span<byte> slug = stackalloc byte[PostFacts.TitleMax];
        return slug[..PostFacts.Slugify(name, slug)].ToArray();
    }

    private static byte[][] SlugsOf(byte[][] names) => Lowered(names);
}
