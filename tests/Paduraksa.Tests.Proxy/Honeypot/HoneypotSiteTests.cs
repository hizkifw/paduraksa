using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using Paduraksa.Proxy.Honeypot;

namespace Paduraksa.Tests.Proxy.Honeypot;

public sealed partial class HoneypotSiteTests
{
    private static readonly HoneypotPageKind[] LinkTargets =
        [HoneypotPageKind.Post, HoneypotPageKind.Category, HoneypotPageKind.Tag, HoneypotPageKind.Author, HoneypotPageKind.Image, HoneypotPageKind.Beacon];

    [Fact]
    public void ASiteKeyAndAUrlMakeTheSameBytesEveryTime()
    {
        var one = SiteCrawl.Site();
        var two = SiteCrawl.Site();
        Assert.Equal(one.Prefix, two.Prefix);
        foreach (var page in SiteCrawl.Crawl(one, 25))
        {
            var again = SiteCrawl.RenderBytes(two, two.Resolve(page.Url == "<landing>" ? "/x" : page.Url));
            if (page.Url != "<landing>")
            {
                Assert.Equal(page.Body, Encoding.UTF8.GetString(again.Bytes));
            }
        }

        var canary = one.Canary([10, 0, 0, 1], "agent");
        var post = FirstPost(one);
        Assert.Equal(SiteCrawl.RenderBytes(one, post, canary).Bytes, SiteCrawl.RenderBytes(two, post, canary).Bytes);
    }

    [Fact]
    public void ADifferentKeyMakesADifferentSite()
    {
        var one = SiteCrawl.Site(keySeed: 3);
        var two = SiteCrawl.Site(keySeed: 4);
        Assert.NotEqual(one.Prefix, two.Prefix);
        Assert.NotEqual(Encoding.UTF8.GetString(SiteCrawl.RenderBytes(one, one.Resolve(one.Prefix)).Bytes).Replace(one.Prefix, "/", StringComparison.Ordinal), Encoding.UTF8.GetString(SiteCrawl.RenderBytes(two, two.Resolve(two.Prefix)).Bytes).Replace(two.Prefix, "/", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePrefixIsAWordAndFourBase32CharactersAndTheNameReadsLikeABlog()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var key = SiteCrawl.MakeKey(seed);
            Assert.Matches("^/[a-z]+-[a-z2-7]{4}/$", HoneypotSite.DerivePrefix(key));
            Assert.Equal(HoneypotSite.DerivePrefix(key), HoneypotSite.DerivePrefix(key));
            var name = HoneypotSite.DeriveSiteName(key);
            Assert.Matches("^[A-Z][A-Za-z ]{5,40}$", name);
        }
    }

    [Fact]
    public void AnOperatorsSiteNameIsEscapedOnce()
    {
        var site = SiteCrawl.Site(name: "Tom & \"Jerry\" <b>");
        var body = SiteCrawl.Page(site, site.Prefix).Body;
        Assert.Contains("Tom &amp; &quot;Jerry&quot; &lt;b&gt;", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ABadKeyPrefixOrShareIsRefused()
    {
        var key = SiteCrawl.TestKey;
        Assert.Throws<ArgumentException>(() => new HoneypotSite(key.AsSpan(0, 16), new HoneypotSiteOptions("/a-b/", "x", 10)));
        Assert.Throws<ArgumentException>(() => new HoneypotSite(key, new HoneypotSiteOptions("a-b/", "x", 10)));
        Assert.Throws<ArgumentException>(() => new HoneypotSite(key, new HoneypotSiteOptions("/a-b", "x", 10)));
        Assert.Throws<ArgumentException>(() => new HoneypotSite(key, new HoneypotSiteOptions("/a\"b/", "x", 10)));
        Assert.Throws<ArgumentException>(() => new HoneypotSite(key, new HoneypotSiteOptions("/a//b/", "x", 10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HoneypotSite(key, new HoneypotSiteOptions("/a-b/", "x", 101)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HoneypotSite(key, new HoneypotSiteOptions("/a-b/", "x", -1)));
    }

    [Fact]
    public void EveryLinkOnEveryPageResolvesToTheKindItWasPublishedAsAndTheRightParent()
    {
        var site = SiteCrawl.Site();
        var seen = new HashSet<HoneypotLinkKind>();
        foreach (var page in SiteCrawl.Crawl(site, 80))
        {
            var noIndex = page.Body.Contains("noindex, nofollow", StringComparison.Ordinal);
            foreach (var link in page.Links)
            {
                Assert.StartsWith(site.Prefix, link.Url, StringComparison.Ordinal);
                var target = site.Resolve(link.Url);
                Assert.NotEqual(HoneypotPageKind.NotFound, target.Kind);
                if (SiteCrawl.IsPlain(link.Url, site.Prefix) || target.Kind is HoneypotPageKind.Sitemap or HoneypotPageKind.Feed)
                {
                    Assert.Equal(HoneypotLinkKind.Direct, target.Via);
                    continue;
                }

                var expected = noIndex && link.Markup is not (HoneypotLinkKind.Image or HoneypotLinkKind.Beacon) ? HoneypotLinkKind.NoIndexPage : link.Markup;
                Assert.Equal(expected, target.Via);
                Assert.Equal(page.Target.PageId, target.ParentId);
                seen.Add(target.Via);
            }
        }

        // Everything a page publishes, across the site. Sitemap and Feed links live in their own documents.
        Assert.Equal(
            [.. new[]
            {
                HoneypotLinkKind.Normal, HoneypotLinkKind.NoFollow, HoneypotLinkKind.Ugc, HoneypotLinkKind.Sponsored, HoneypotLinkKind.Hidden, HoneypotLinkKind.Comment,
                HoneypotLinkKind.Script, HoneypotLinkKind.Beacon, HoneypotLinkKind.NoIndexPage, HoneypotLinkKind.Image,
            }.Order()],
            seen.Order());
    }

    [Fact]
    public void EveryKindOfPageThatLinksCarriesEveryKindOfLink()
    {
        var site = SiteCrawl.Site(powShare: 0);
        var pages = SiteCrawl.Crawl(site, 120);
        var needed = new[]
        {
            HoneypotLinkKind.Normal, HoneypotLinkKind.NoFollow, HoneypotLinkKind.Ugc, HoneypotLinkKind.Sponsored, HoneypotLinkKind.Hidden, HoneypotLinkKind.Comment,
            HoneypotLinkKind.Script, HoneypotLinkKind.Beacon, HoneypotLinkKind.Image,
        };
        var kindsChecked = new HashSet<HoneypotPageKind>();
        foreach (var page in pages.Where(p => p.Render.ContentType.StartsWith("text/html", StringComparison.Ordinal) && !p.Body.Contains("noindex", StringComparison.Ordinal)))
        {
            var present = page.Links.Select(l => l.Markup).ToHashSet();
            foreach (var kind in needed)
            {
                Assert.True(present.Contains(kind), $"{page.Target.Kind} page {page.Url} has no {kind} link");
            }

            // Most links are plain ones.
            var normal = page.Links.Count(l => l.Markup == HoneypotLinkKind.Normal);
            Assert.True(normal * 2 > page.Links.Count, $"{page.Url}: {normal} of {page.Links.Count} links are normal");
            _ = kindsChecked.Add(page.Target.Kind);
        }

        Assert.Superset(
            new HashSet<HoneypotPageKind>
            {
                HoneypotPageKind.Index, HoneypotPageKind.Post, HoneypotPageKind.Category, HoneypotPageKind.Tag, HoneypotPageKind.Author, HoneypotPageKind.NotFound, HoneypotPageKind.Landing,
            },
            kindsChecked);
    }

    [Fact]
    public void AboutOneInTenPagesIsNoIndexAndItsLinksSayTheyAre()
    {
        var site = SiteCrawl.Site(powShare: 0);
        var pages = SiteCrawl.Crawl(site, 300).Where(p => p.Target.Kind is HoneypotPageKind.Post or HoneypotPageKind.Category or HoneypotPageKind.Tag or HoneypotPageKind.IndexPage).ToList();
        var noIndex = pages.Where(p => p.Body.Contains("<meta name=\"robots\" content=\"noindex, nofollow\">", StringComparison.Ordinal)).ToList();
        Assert.InRange(noIndex.Count, pages.Count / 25, pages.Count / 4);
        Assert.NotEmpty(noIndex);
        foreach (var page in noIndex)
        {
            Assert.Contains(page.Links, l => l.Markup == HoneypotLinkKind.Image);
            Assert.DoesNotContain(page.Links, l => site.Resolve(l.Url).Via is HoneypotLinkKind.Normal or HoneypotLinkKind.NoFollow or HoneypotLinkKind.Hidden or HoneypotLinkKind.Ugc);
            Assert.Contains(page.Links, l => site.Resolve(l.Url).Via == HoneypotLinkKind.NoIndexPage);
        }

        // The entry pages never are.
        Assert.DoesNotContain("noindex", SiteCrawl.Page(site, site.Prefix).Body, StringComparison.Ordinal);
        Assert.DoesNotContain("noindex", SiteCrawl.Page(site, "<landing>").Body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSitemapAndTheFeedCarryTheirOwnLinkKinds()
    {
        var site = SiteCrawl.Site();
        var sitemap = SiteCrawl.Page(site, site.Prefix + "sitemap.xml");
        var feed = SiteCrawl.Page(site, site.Prefix + "feed/");
        var sitemapUrls = Regex.Matches(sitemap.Body, "<loc>([^<]+)</loc>").Select(m => m.Groups[1].Value).ToList();
        var feedUrls = Regex.Matches(feed.Body, "<link>([^<]+)</link>").Select(m => m.Groups[1].Value).Skip(1).ToList();
        Assert.Equal(1 + 8 + 24 + 4 + 40, sitemapUrls.Count);
        Assert.Equal(20, feedUrls.Count);
        foreach (var url in sitemapUrls.Skip(1))
        {
            var target = site.Resolve(url);
            Assert.Equal(HoneypotLinkKind.Sitemap, target.Via);
            Assert.Equal(HoneypotPageKind.Sitemap, (HoneypotPageKind)(target.ParentId >> 28));
        }

        foreach (var url in feedUrls)
        {
            var target = site.Resolve(url);
            Assert.Equal(HoneypotPageKind.Post, target.Kind);
            Assert.Equal(HoneypotLinkKind.Feed, target.Via);
            Assert.Equal(HoneypotPageKind.Feed, (HoneypotPageKind)(target.ParentId >> 28));
        }

        // Some of them appear nowhere else.
        var onPages = SiteCrawl.Crawl(site, 40).SelectMany(p => p.Links).Select(l => l.Url).ToHashSet();
        Assert.Contains(sitemapUrls, u => !onPages.Contains(u));
        Assert.Contains(feedUrls, u => !onPages.Contains(u));
    }

    [Fact]
    public void EveryKindOfLinkRoundTripsThroughItsToken()
    {
        var site = SiteCrawl.Site();
        var post = SiteCrawl.Page(site, site.Prefix).Links.Select(l => (l.Url, Target: site.Resolve(l.Url))).First(x => x.Target.Kind == HoneypotPageKind.Post);
        Assert.Equal(HoneypotLinkKind.Normal, post.Target.Via);
        Assert.Equal(0UL, post.Target.ParentId);
        var kinds = new HashSet<HoneypotPageKind>();
        foreach (var page in SiteCrawl.Crawl(site, 60))
        {
            foreach (var link in page.Links.Where(l => !SiteCrawl.IsPlain(l.Url, site.Prefix)))
            {
                var target = site.Resolve(link.Url);
                _ = kinds.Add(target.Kind);

                // The ID is part of the URL: the same URL, resolved again, is the same target.
                Assert.Equal(target, site.Resolve(link.Url));
            }
        }

        Assert.Superset(LinkTargets.ToHashSet(), kinds);
    }

    [Fact]
    public void ATamperedGarbledOrOversizedPathIsNotFoundAndNeverThrows()
    {
        var site = SiteCrawl.Site();
        var urls = SiteCrawl.Crawl(site, 20).SelectMany(p => p.Links).Select(l => l.Url).Where(u => !SiteCrawl.IsPlain(u, site.Prefix)).Distinct().Take(60).ToList();
        Assert.NotEmpty(urls);
        foreach (var url in urls)
        {
            Assert.NotEqual(HoneypotPageKind.NotFound, site.Resolve(url).Kind);

            // Every character of the token and of the text before it, changed in turn.
            var tokenStart = url.LastIndexOf('-') + 1;
            for (var i = site.Prefix.Length; i < url.Length; i++)
            {
                if (!char.IsAsciiLetterOrDigit(url[i]))
                {
                    continue;
                }

                var replacement = url[i] == 'x' ? 'y' : 'x';
                var tampered = string.Concat(url.AsSpan(0, i), replacement.ToString(), url.AsSpan(i + 1));
                Assert.True(site.Resolve(tampered).Kind == HoneypotPageKind.NotFound, $"{tampered} resolved (changed index {i}, token at {tokenStart})");
            }

            // A token that is cut, grown, upper-cased or moved to another folder.
            var withoutLast = url.Remove(url.Length - 2, 1);
            Assert.Equal(HoneypotPageKind.NotFound, site.Resolve(withoutLast).Kind);
            Assert.Equal(HoneypotPageKind.NotFound, site.Resolve(url.Insert(url.Length - 1, "a")).Kind);
            Assert.Equal(HoneypotPageKind.NotFound, site.Resolve(string.Concat(url.AsSpan(0, site.Prefix.Length), url.AsSpan(site.Prefix.Length).ToString().ToUpperInvariant())).Kind);
            Assert.Equal(HoneypotPageKind.NotFound, site.Resolve(url + "x").Kind);
            Assert.Equal(HoneypotPageKind.NotFound, site.Resolve(url + "/").Kind);
            Assert.Equal(HoneypotPageKind.NotFound, site.Resolve(url.Replace(site.Prefix, site.Prefix + "x/", StringComparison.Ordinal)).Kind);
        }

        string[] garbage =
        [
            string.Empty, "/", "/x", site.Prefix + "/", site.Prefix + "//", site.Prefix + "a/b/c/d/e/", site.Prefix + "page/", site.Prefix + "page/0/", site.Prefix + "page/1/", site.Prefix + "page/02/",
            site.Prefix + "page/100000/", site.Prefix + "page/-3/", site.Prefix + "page/2", site.Prefix + "page/99999999999999999999/", site.Prefix + "category/", site.Prefix + "category//",
            site.Prefix + "category/x-aaaaaaaaaaaaaaaaaaaa/", site.Prefix + "2024/07/x-aaaaaaaaaaaaaaaaaaaa/", site.Prefix + "2024/07/", site.Prefix + "media/.svg", site.Prefix + "media/x-aaaaaaaaaaaaaaaaaaaa.svg",
            site.Prefix + "assets/stats-aaaaaaaaaaaaaaaaaaaa.json", site.Prefix + "assets/stats-.json", site.Prefix + "sitemap.xml/", site.Prefix + "feed", site.Prefix + "\0", site.Prefix + "\u00e9\u00e9/",
            site.Prefix + new string('a', 5000), site.Prefix + "2024/07/" + new string('a', 5000) + "/", "/other/", site.Prefix[..^1],
        ];
        foreach (var path in garbage)
        {
            var target = site.Resolve(path);
            Assert.True(
                target.Kind == HoneypotPageKind.NotFound || path == site.Prefix || path == site.Prefix + "sitemap.xml",
                $"'{path}' resolved to {target.Kind}");
        }

        // A site with another key never accepts this one's tokens.
        var other = SiteCrawl.Site(keySeed: 9);
        var sameShape = new HoneypotSite(SiteCrawl.TestKey, new HoneypotSiteOptions(other.Prefix, "x", 0));
        var foreign = SiteCrawl.Page(sameShape, other.Prefix).Links.First(l => !SiteCrawl.IsPlain(l.Url, other.Prefix)).Url;
        Assert.Equal(HoneypotPageKind.NotFound, other.Resolve(foreign).Kind);
    }

    [Fact]
    public void ARandomlyDamagedUrlNeverResolvesToAnotherPage()
    {
        var site = SiteCrawl.Site();
        var urls = SiteCrawl.Crawl(site, 10).SelectMany(p => p.Links).Select(l => l.Url).Where(u => !SiteCrawl.IsPlain(u, site.Prefix)).Distinct().Take(40).ToList();
        var random = new Random(7);
        var resolved = 0;
        foreach (var url in urls)
        {
            for (var round = 0; round < 100; round++)
            {
                var chars = url.ToCharArray();
                var changes = random.Next(1, 4);
                for (var c = 0; c < changes; c++)
                {
                    chars[random.Next(site.Prefix.Length, chars.Length)] = (char)random.Next(32, 127);
                }

                var damaged = new string(chars);
                if (damaged != url && site.Resolve(damaged).Kind != HoneypotPageKind.NotFound)
                {
                    resolved++;
                }
            }
        }

        // The tag is 36 bits, so a hit in four thousand tries is a bug. Damage that only touches a folder name or a page number can legitimately land on a real page.
        Assert.True(resolved <= 20, $"{resolved} of {urls.Count * 100} damaged URLs resolved");
    }

    [Fact]
    public void AnythingOutsideThePrefixIsNotFound()
    {
        var site = SiteCrawl.Site();
        Assert.Equal(HoneypotPageKind.NotFound, site.Resolve("/robots.txt").Kind);
        Assert.Equal(HoneypotPageKind.NotFound, site.Resolve(site.Prefix.ToUpperInvariant()).Kind);
        Assert.Equal(HoneypotPageKind.Index, site.Resolve(site.Prefix).Kind);
    }

    [Fact]
    public void PowShareZeroChallengesNothingAndOneHundredEveryPostCategoryTagAndAuthor()
    {
        var none = SiteCrawl.Site(powShare: 0);
        var all = SiteCrawl.Site(powShare: 100);
        var half = SiteCrawl.Site(powShare: 20);
        var urls = SiteCrawl.Crawl(none, 150).SelectMany(p => p.Links).Select(l => l.Url).Distinct().ToList();
        var challengeable = 0;
        foreach (var url in urls)
        {
            Assert.False(none.Resolve(url).NeedsPow, url);
            var target = all.Resolve(url);
            var expected = target.Kind is HoneypotPageKind.Post or HoneypotPageKind.Category or HoneypotPageKind.Tag or HoneypotPageKind.Author;
            Assert.Equal(expected, target.NeedsPow);
            challengeable += expected ? 1 : 0;
        }

        Assert.True(challengeable > 50);
        var posts = urls.Where(u => all.Resolve(u).Kind == HoneypotPageKind.Post).ToList();
        var share = posts.Count(u => half.Resolve(u).NeedsPow) * 100.0 / posts.Count;
        Assert.InRange(share, 8, 35);

        // The same page is always the same answer.
        Assert.All(posts, u => Assert.Equal(half.Resolve(u).NeedsPow, half.Resolve(u).NeedsPow));

        // Entry pages and resources are never challenged, even at 100.
        Assert.False(all.Resolve(all.Prefix).NeedsPow);
        Assert.False(all.Resolve(all.Prefix + "page/2/").NeedsPow);
        Assert.False(all.Resolve(all.Prefix + "sitemap.xml").NeedsPow);
        Assert.False(all.Resolve(all.Prefix + "feed/").NeedsPow);
        Assert.False(all.Resolve(all.Prefix + "nothing").NeedsPow);
        Assert.False(all.Landing("/wp-login.php").NeedsPow);
        Assert.DoesNotContain(urls, u => all.Resolve(u).Kind is HoneypotPageKind.Image or HoneypotPageKind.Beacon && all.Resolve(u).NeedsPow);
    }

    [Fact]
    public void EveryPageIsWellFormedHtml()
    {
        var site = SiteCrawl.Site();
        var pages = SiteCrawl.Crawl(site, 120).Concat([SiteCrawl.Page(site, site.Prefix + "page/2/"), SiteCrawl.Page(site, site.Prefix + "page/40/")]).ToList();
        Assert.True(pages.Count > 100);
        foreach (var page in pages)
        {
            Assert.Equal("text/html; charset=utf-8", page.Render.ContentType);
            Assert.StartsWith("<!DOCTYPE html>", page.Body, StringComparison.Ordinal);
            Assert.EndsWith("</html>\n", page.Body, StringComparison.Ordinal);
            AssertBalanced(page.Body, page.Url);
            Assert.Equal(page.Target.Kind == HoneypotPageKind.NotFound ? 404 : 200, page.Render.Status);
        }
    }

    [Fact]
    public void TheSitemapTheFeedAndAnImageAreValidXml()
    {
        var site = SiteCrawl.Site();
        var sitemap = XDocument.Parse(SiteCrawl.Page(site, site.Prefix + "sitemap.xml").Body);
        Assert.Equal("urlset", sitemap.Root!.Name.LocalName);
        Assert.Equal(77, sitemap.Root.Elements().Count());

        var feedPage = SiteCrawl.Page(site, site.Prefix + "feed/");
        Assert.Equal("application/rss+xml; charset=utf-8", feedPage.Render.ContentType);
        var feed = XDocument.Parse(feedPage.Body);
        Assert.Equal("rss", feed.Root!.Name.LocalName);
        Assert.Equal("2.0", feed.Root.Attribute("version")!.Value);
        var items = feed.Root.Element("channel")!.Elements("item").ToList();
        Assert.Equal(20, items.Count);
        Assert.All(items, i =>
        {
            Assert.NotEmpty(i.Element("title")!.Value);
            Assert.True(DateTimeOffset.TryParseExact(i.Element("pubDate")!.Value, "ddd, dd MMM yyyy HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _), i.Element("pubDate")!.Value);
        });

        var image = SiteCrawl.Page(site, SiteCrawl.Links(SiteCrawl.Page(site, site.Prefix).Body, site.Prefix).First(l => l.Markup == HoneypotLinkKind.Image).Url);
        Assert.Equal("image/svg+xml", image.Render.ContentType);
        Assert.Equal(200, image.Render.Status);
        Assert.Equal("svg", XDocument.Parse(image.Body).Root!.Name.LocalName);
        Assert.InRange(image.Body.Length, 300, 3000);
    }

    [Fact]
    public void TheBeaconAnswersWithATinyJsonBody()
    {
        var site = SiteCrawl.Site();
        var url = SiteCrawl.Page(site, site.Prefix).Links.First(l => l.Markup == HoneypotLinkKind.Beacon).Url;
        var page = SiteCrawl.Page(site, url);
        Assert.Equal(HoneypotPageKind.Beacon, page.Target.Kind);
        Assert.Equal(200, page.Render.Status);
        Assert.Equal("application/json", page.Render.ContentType);
        Assert.Equal("{}", page.Body);
    }

    [Fact]
    public void NothingGeneratedNamesPaduraksaOrTheGenerator()
    {
        var site = SiteCrawl.Site();
        var all = SiteCrawl.Crawl(site, 60).Select(p => p.Body).Concat([
            SiteCrawl.Page(site, site.Prefix + "sitemap.xml").Body,
            SiteCrawl.Page(site, site.Prefix + "feed/").Body,
        ]);
        foreach (var body in all)
        {
            Assert.DoesNotContain("paduraksa", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("honeypot", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("generated", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("http://", body.Replace("http://www.w3.org/2000/svg", string.Empty, StringComparison.Ordinal).Replace("http://www.sitemaps.org/schemas/sitemap/0.9", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.DoesNotContain("https://", body, StringComparison.Ordinal);
        }

        for (var seed = 0; seed < 200; seed++)
        {
            var key = SiteCrawl.MakeKey(seed);
            Assert.DoesNotContain("paduraksa", HoneypotSite.DerivePrefix(key) + HoneypotSite.DeriveSiteName(key), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void PagesStayWithinTheirSizeBounds()
    {
        var site = SiteCrawl.Site();
        var pages = SiteCrawl.Crawl(site, 150);
        var posts = pages.Where(p => p.Target.Kind == HoneypotPageKind.Post).ToList();
        Assert.True(posts.Count > 30);
        foreach (var post in posts)
        {
            Assert.InRange(Encoding.UTF8.GetByteCount(post.Body), 4_000, 14_000);
        }

        foreach (var page in pages)
        {
            Assert.InRange(Encoding.UTF8.GetByteCount(page.Body), 3_000, 24_000);
        }
    }

    [Fact]
    public void TheCanaryAppearsOncePerPostAndNowhereWithoutOne()
    {
        var site = SiteCrawl.Site();
        var canary = site.Canary([203, 0, 113, 9], "Mozilla/5.0 (X11; Linux x86_64) Chrome/130");
        Assert.Matches("^[a-z]{6,12} [a-z]{6,12}$", canary);
        Assert.Equal(canary, site.Canary([203, 0, 113, 9], "Mozilla/5.0 (X11; Linux x86_64) Chrome/130"));
        Assert.NotEqual(canary, site.Canary([203, 0, 113, 10], "Mozilla/5.0 (X11; Linux x86_64) Chrome/130"));
        Assert.NotEqual(canary, site.Canary([203, 0, 113, 9], "curl/8.0"));
        Assert.NotEqual(canary, SiteCrawl.Site(keySeed: 5).Canary([203, 0, 113, 9], "Mozilla/5.0 (X11; Linux x86_64) Chrome/130"));
        Assert.NotEmpty(site.Canary([], string.Empty));
        Assert.NotEmpty(site.Canary(new byte[16], new string('a', 100_000)));

        var posts = SiteCrawl.Crawl(site, 150).Where(p => p.Target.Kind is HoneypotPageKind.Post or HoneypotPageKind.Landing).Select(p => p.Url).ToList();
        Assert.True(posts.Count > 25);
        foreach (var url in posts)
        {
            var target = url == "<landing>" ? site.Landing("/wp-login.php") : site.Resolve(url);
            var with = Encoding.UTF8.GetString(SiteCrawl.RenderBytes(site, target, canary).Bytes);
            var without = Encoding.UTF8.GetString(SiteCrawl.RenderBytes(site, target).Bytes);
            Assert.Equal(1, Occurrences(with, canary));
            Assert.Equal(0, Occurrences(without, canary));

            // It sits inside a paragraph of the prose, not in an attribute or a heading.
            var at = with.IndexOf(canary, StringComparison.Ordinal);
            Assert.True(with.LastIndexOf("<p>", at, StringComparison.Ordinal) > with.LastIndexOf("</p>", at, StringComparison.Ordinal), url);
            Assert.True(with.IndexOf("</p>", at, StringComparison.Ordinal) < with.IndexOf('<', at) + 1, url);
            Assert.True(with.LastIndexOf('<', at) <= with.LastIndexOf('>', at), url);

            // Taking the canary's sentence out leaves the page a visitor without one gets: the phrase draws nothing from the page's generator.
            Assert.Equal(without, Regex.Replace(with, " (Some readers call it the |A friend of mine calls this |In the old notes it was simply known as |People keep asking about )" + Regex.Escape(canary) + "( method\\.|, and the name stuck\\.|\\.|, so here it is\\.)", string.Empty));
        }

        // Other pages take no canary.
        var index = SiteCrawl.Page(site, site.Prefix, canary);
        Assert.DoesNotContain(canary, index.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void ACanaryIsOnlyEverLettersDigitsAndSpaces()
    {
        var site = SiteCrawl.Site();
        var post = FirstPost(site);
        var body = Encoding.UTF8.GetString(SiteCrawl.RenderBytes(site, post, "<script>alert(1)</script> \"x\"").Bytes);
        Assert.DoesNotContain("alert(1)</script>", body, StringComparison.Ordinal);
        Assert.Contains("scriptalert1script x", body, StringComparison.Ordinal);
        AssertBalanced(body, "hostile canary");
    }

    [Fact]
    public void ADateIsDerivedPerPostAndItsUrlAgrees()
    {
        var site = SiteCrawl.Site();
        var posts = SiteCrawl.Crawl(site, 100).Where(p => p.Target.Kind == HoneypotPageKind.Post).ToList();
        Assert.True(posts.Count > 20);
        foreach (var post in posts)
        {
            var modified = post.Render.LastModified!.Value;
            Assert.InRange(modified.Year, 2021, 2026);
            var match = Regex.Match(post.Url, "/(\\d{4})/(\\d{2})/");
            Assert.Equal(modified.Year, int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(modified.Month, int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Contains($"datetime=\"{modified:yyyy-MM-dd}\"", post.Body, StringComparison.Ordinal);
            Assert.Equal(TimeSpan.Zero, modified.Offset);
        }

        Assert.True(posts.Select(p => p.Render.LastModified!.Value.Year).Distinct().Count() >= 4);
        Assert.Null(SiteCrawl.Page(site, site.Prefix).Render.LastModified);
        Assert.Null(SiteCrawl.Page(site, site.Prefix + "no-such").Render.LastModified);
    }

    [Fact]
    public void ALandingPageIsABlogPageForTheProbedPathThatLinksIntoThePrefix()
    {
        var site = SiteCrawl.Site();
        var landing = site.Landing("/wp-login.php");
        Assert.Equal(HoneypotPageKind.Landing, landing.Kind);
        Assert.Equal(HoneypotLinkKind.Direct, landing.Via);
        Assert.Equal(landing, site.Landing("/wp-login.php"));
        Assert.Equal(landing, site.Landing("/WP-LOGIN.php"));
        Assert.NotEqual(landing.PageId, site.Landing("/xmlrpc.php").PageId);
        Assert.Equal(0UL, landing.ParentId);

        var page = SiteCrawl.Page(site, "<landing>");
        Assert.Equal(200, page.Render.Status);
        Assert.NotNull(page.Render.LastModified);
        Assert.Contains("<article>", page.Body, StringComparison.Ordinal);
        Assert.True(page.Links.Count(l => l.Markup == HoneypotLinkKind.Normal) > 10);
        Assert.All(page.Links, l => Assert.StartsWith(site.Prefix, l.Url, StringComparison.Ordinal));
        Assert.All(page.Links.Where(l => !SiteCrawl.IsPlain(l.Url, site.Prefix)), l => Assert.Equal(landing.PageId, site.Resolve(l.Url).ParentId));
    }

    [Fact]
    public void ThePaginationLinksWalkTheIndexWithoutAnEnd()
    {
        var site = SiteCrawl.Site();
        var first = SiteCrawl.Page(site, site.Prefix);
        Assert.Contains($"href=\"{site.Prefix}page/2/\"", first.Body, StringComparison.Ordinal);
        var second = SiteCrawl.Page(site, site.Prefix + "page/2/");
        Assert.Equal(HoneypotPageKind.IndexPage, second.Target.Kind);
        Assert.Contains($"href=\"{site.Prefix}\">Newer", second.Body, StringComparison.Ordinal);
        Assert.Contains($"href=\"{site.Prefix}page/3/\"", second.Body, StringComparison.Ordinal);
        var third = SiteCrawl.Page(site, site.Prefix + "page/3/");
        Assert.Contains($"href=\"{site.Prefix}page/2/\">Newer", third.Body, StringComparison.Ordinal);
        Assert.NotEqual(first.Links.Select(l => l.Url).Order(), third.Links.Select(l => l.Url).Order());
        var last = SiteCrawl.Page(site, site.Prefix + "page/99999/");
        Assert.Equal(HoneypotPageKind.IndexPage, last.Target.Kind);
        Assert.DoesNotContain("page/100000/", last.Body, StringComparison.Ordinal);
        Assert.All(last.Links.Where(l => l.Url.StartsWith(site.Prefix, StringComparison.Ordinal)), l => Assert.NotEqual(HoneypotPageKind.NotFound, site.Resolve(l.Url).Kind));
    }

    [Fact]
    public void AHandBuiltTargetThatNamesNothingGetsTheNotFoundPage()
    {
        var site = SiteCrawl.Site();
        var made = new HoneypotTarget(HoneypotPageKind.Category, HoneypotLinkKind.Normal, HoneypotSite.Pack(HoneypotPageKind.Category, 999), 0, false);
        var (render, bytes) = SiteCrawl.RenderBytes(site, made);
        Assert.Equal(404, render.Status);
        Assert.Contains("Page not found", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void AGoldenPageStaysTheSameAcrossBuildsAndRuntimes()
    {
        // The site must stay byte-stable across .NET versions: a crawler that revisits a URL a year later should see the same page. If this fails
        // after a change to the lexicon, the templates or the renderer, the change moved every page of every deployed trap; update the hashes on purpose.
        var site = SiteCrawl.Site();
        var post = SiteCrawl.Page(site, site.Prefix).Links.Where(l => l.Markup == HoneypotLinkKind.Normal).Select(l => l.Url).First(u => site.Resolve(u).Kind == HoneypotPageKind.Post);
        Assert.Equal(
            ("605AC87FB09D14F1", "180A1323CBD33306", "1D66C2A937F20939"),
            (Hash(site, post), Hash(site, site.Prefix), Hash(site, site.Prefix + "sitemap.xml")));
    }

    private static string Hash(HoneypotSite site, string url) => Convert.ToHexString(SHA256.HashData(SiteCrawl.RenderBytes(site, site.Resolve(url), "fox trot").Bytes))[..16];

    private static HoneypotTarget FirstPost(HoneypotSite site) =>
        SiteCrawl.Page(site, site.Prefix).Links.Select(l => site.Resolve(l.Url)).First(t => t.Kind == HoneypotPageKind.Post);

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static readonly HashSet<string> VoidElements = ["meta", "link", "img", "br", "hr", "input"];

    private static void AssertBalanced(string html, string where)
    {
        // Script and style hold text this reader would take for tags.
        var scrubbed = ScriptOrStyleBody().Replace(HtmlComment().Replace(html, string.Empty), m => $"<{m.Groups[1].Value}></{m.Groups[1].Value}>");
        Stack<string> open = [];
        foreach (Match m in Tag().Matches(scrubbed))
        {
            var closing = m.Groups[1].Value == "/";
            var name = m.Groups[2].Value.ToLowerInvariant();
            if (VoidElements.Contains(name))
            {
                Assert.False(closing, $"{where}: </{name}>");
                continue;
            }

            if (closing)
            {
                Assert.True(open.Count > 0 && open.Peek() == name, $"{where}: </{name}> closes <{(open.Count > 0 ? open.Peek() : "nothing")}>");
                _ = open.Pop();
            }
            else
            {
                open.Push(name);
            }
        }

        Assert.True(open.Count == 0, $"{where}: still open: {string.Join(",", open)}");
    }

    [GeneratedRegex("<(/?)([a-zA-Z][a-zA-Z0-9]*)[^>]*>")]
    private static partial Regex Tag();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();

    [GeneratedRegex("<(script|style)>.*?</\\1>", RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyleBody();
}
