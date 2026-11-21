using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

using Paduraksa.Proxy.Honeypot;

namespace Paduraksa.Tests.Proxy.Honeypot;

/// <summary>A rendered page and the links it publishes, each with the kind its markup says it is.</summary>
internal sealed record CrawledPage(string Url, HoneypotTarget Target, HoneypotRender Render, string Body, IReadOnlyList<PageLink> Links);

/// <summary>One link of a page: where it points and the kind its markup promises.</summary>
internal sealed record PageLink(string Url, HoneypotLinkKind Markup);

/// <summary>Walks a trap site the way a crawler would, with the real renderer and a markup reader independent of it.</summary>
internal static partial class SiteCrawl
{
    public static readonly byte[] TestKey = MakeKey(3);

    public static byte[] MakeKey(int seed)
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++)
        {
            key[i] = (byte)((i * 7) + seed);
        }

        return key;
    }

    public static HoneypotSite Site(int powShare = 20, int keySeed = 3, string? name = null)
    {
        var key = MakeKey(keySeed);
        return new HoneypotSite(key, new HoneypotSiteOptions(HoneypotSite.DerivePrefix(key), name ?? HoneypotSite.DeriveSiteName(key), powShare));
    }

    public static (HoneypotRender Render, byte[] Bytes) RenderBytes(HoneypotSite site, HoneypotTarget target, string canary = "")
    {
        var writer = new ArrayBufferWriter<byte>();
        var render = site.Render(target, canary, writer);
        return (render, writer.WrittenSpan.ToArray());
    }

    public static CrawledPage Page(HoneypotSite site, string url, string canary = "")
    {
        var target = url == "<landing>" ? site.Landing("/wp-login.php") : site.Resolve(url);
        var (render, bytes) = RenderBytes(site, target, canary);
        var body = Encoding.UTF8.GetString(bytes);
        return new CrawledPage(url, target, render, body, render.ContentType.StartsWith("text/html", StringComparison.Ordinal) ? Links(body, site.Prefix) : []);
    }

    /// <summary>Every page reachable from the index within <paramref name="limit"/> pages, breadth first, plus the 404.</summary>
    public static List<CrawledPage> Crawl(HoneypotSite site, int limit)
    {
        List<CrawledPage> pages = [];
        HashSet<string> seen = [site.Prefix];
        Queue<string> queue = new([site.Prefix]);
        while (queue.Count > 0 && pages.Count < limit)
        {
            var page = Page(site, queue.Dequeue());
            pages.Add(page);
            foreach (var link in page.Links)
            {
                if (seen.Add(link.Url) && site.Resolve(link.Url).Kind is not (HoneypotPageKind.Sitemap or HoneypotPageKind.Feed or HoneypotPageKind.Image or HoneypotPageKind.Beacon))
                {
                    queue.Enqueue(link.Url);
                }
            }
        }

        pages.Add(Page(site, site.Prefix + "no-such-page"));
        pages.Add(Page(site, "<landing>"));
        return pages;
    }

    public static List<PageLink> Links(string html, string prefix)
    {
        List<PageLink> links = [];
        foreach (Match m in Anchor().Matches(html))
        {
            var url = m.Groups[1].Value;
            var attributes = m.Groups[2].Value;
            if (!url.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            links.Add(new PageLink(
                url,
                attributes.Contains("ugc", StringComparison.Ordinal) ? HoneypotLinkKind.Ugc
                : attributes.Contains("sponsored", StringComparison.Ordinal) ? HoneypotLinkKind.Sponsored
                : attributes.Contains("class=\"hd\"", StringComparison.Ordinal) ? HoneypotLinkKind.Hidden
                : attributes.Contains("nofollow", StringComparison.Ordinal) ? HoneypotLinkKind.NoFollow
                : HoneypotLinkKind.Normal));
        }

        foreach (Match m in Image().Matches(html))
        {
            links.Add(new PageLink(m.Groups[1].Value, HoneypotLinkKind.Image));
        }

        foreach (Match m in ScriptLink().Matches(html))
        {
            links.Add(new PageLink(m.Groups[1].Value + m.Groups[2].Value, HoneypotLinkKind.Script));
        }

        foreach (Match m in BeaconFetch().Matches(html))
        {
            links.Add(new PageLink(m.Groups[1].Value + m.Groups[2].Value, HoneypotLinkKind.Beacon));
        }

        foreach (Match m in HtmlComment().Matches(html))
        {
            links.Add(new PageLink(m.Groups[1].Value, HoneypotLinkKind.Comment));
        }

        return links;
    }

    /// <summary>Whether a URL carries a token, which pagination, the index, the feed and the sitemap do not.</summary>
    public static bool IsPlain(string url, string prefix)
    {
        var rest = url[prefix.Length..];
        return rest.Length == 0 || rest is "feed/" or "sitemap.xml" || rest.StartsWith("page/", StringComparison.Ordinal);
    }

    [GeneratedRegex("<a href=\"([^\"]*)\"([^>]*)>")]
    private static partial Regex Anchor();

    [GeneratedRegex("<img src=\"([^\"]*)\"")]
    private static partial Regex Image();

    [GeneratedRegex("a\\.href=\"([^\"]*)\"\\+\"([^\"]*)\"")]
    private static partial Regex ScriptLink();

    [GeneratedRegex("fetch\\(\"([^\"]*)\"\\+\"([^\"]*)\"")]
    private static partial Regex BeaconFetch();

    [GeneratedRegex("<!-- (\\S+) -->")]
    private static partial Regex HtmlComment();
}
