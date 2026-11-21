using System.Buffers;

namespace Paduraksa.Proxy.Honeypot;

/// <summary>
/// Writes one page of the trap site into an <see cref="IBufferWriter{T}"/>. A page draws everything from its own generator in a fixed order,
/// so the bytes depend on the site key, the page and the canary only. Links are written with the kind they are published as, and every page that
/// links carries every kind: that is what lets a crawler's path through the site say what it follows.
/// </summary>
internal ref partial struct PageRenderer
{
    private const string ContentTypeHtml = "text/html; charset=utf-8";
    private const int CardsPerListing = 8;

    private static readonly string[] MonthNames =
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    private readonly HoneypotSite _site;
    private readonly HoneypotPageKind _kind;
    private readonly uint _local;
    private readonly ulong _self;
    private readonly bool _noIndex;
    private readonly ReadOnlySpan<byte> _canary;
    private Utf8Out _o;
    private SiteRandom _rng;

    private PageRenderer(HoneypotSite site, HoneypotPageKind kind, uint local, ReadOnlySpan<byte> canary, IBufferWriter<byte> output)
    {
        _site = site;
        _kind = kind;
        _local = local;
        _self = HoneypotSite.Pack(kind, local);
        _noIndex = site.IsNoIndex(kind, _self);
        _canary = canary;
        _o = new Utf8Out(output);
        _rng = SiteKeys.PageRandom(site.Key, kind, local);
    }

    public static HoneypotRender Render(HoneypotSite site, in HoneypotTarget target, ReadOnlySpan<char> canary, IBufferWriter<byte> output)
    {
        var kind = target.Kind;
        var local = (uint)(target.PageId & SiteToken.MaxLocal);

        // A target made by hand can name a category, tag or author the site doesn't have; it gets the 404 a mangled URL would.
        var known = kind switch
        {
            HoneypotPageKind.Category => local < site.CategoryNames.Length,
            HoneypotPageKind.Tag => local < site.TagNames.Length,
            HoneypotPageKind.Author => local < site.AuthorNames.Length,
            _ => true,
        };
        if (!known)
        {
            kind = HoneypotPageKind.NotFound;
            local = 0;
        }

        // The canary is the visitor's, and a trap that echoed anything else would be a reflection point: only letters, digits and spaces go in.
        Span<byte> canaryBytes = stackalloc byte[64];
        var length = 0;
        foreach (var c in canary)
        {
            if (length < canaryBytes.Length && (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or ' '))
            {
                canaryBytes[length++] = (byte)c;
            }
        }

        var renderer = new PageRenderer(site, kind, local, canaryBytes[..length], output);
        return kind switch
        {
            HoneypotPageKind.Index or HoneypotPageKind.IndexPage => renderer.RenderIndex(),
            HoneypotPageKind.Post or HoneypotPageKind.Landing => renderer.RenderPost(),
            HoneypotPageKind.Category or HoneypotPageKind.Tag or HoneypotPageKind.Author => renderer.RenderListing(),
            HoneypotPageKind.Sitemap => renderer.RenderSitemap(),
            HoneypotPageKind.Feed => renderer.RenderFeed(),
            HoneypotPageKind.Image => renderer.RenderImage(),
            HoneypotPageKind.Beacon => renderer.RenderBeacon(),
            _ => renderer.RenderNotFound(),
        };
    }

    // ---- pages ----

    private HoneypotRender RenderIndex()
    {
        Span<byte> title = stackalloc byte[24];
        var page = _kind == HoneypotPageKind.Index ? 1 : (int)_local;
        if (page == 1)
        {
            Begin(_site.NameBytes, default, withSite: false);
            _o.Write("<section><h1>"u8);
            _o.Write(_site.NameBytes);
            _o.Write("</h1><p class=\"meta\">"u8);
            TextGen.Sentence(ref _o, ref _rng);
            _o.Write("</p></section>"u8);
        }
        else
        {
            var t = new Utf8Out(title);
            t.Write("Page "u8);
            t.WriteInt(page);
            Begin(title[..t.Length], default, withSite: true);
            _o.Write("<h1>Archive, page "u8);
            _o.WriteInt(page);
            _o.Write("</h1>"u8);
        }

        Cards(CardsPerListing);
        _o.Write("<nav class=\"pager\">"u8);
        if (page == 1)
        {
            _o.Write("<span></span>"u8);
        }
        else
        {
            _o.Write("<a href=\""u8);
            _o.Write(_site.PrefixBytes);
            if (page > 2)
            {
                _o.Write("page/"u8);
                _o.WriteInt(page - 1);
                _o.Write((byte)'/');
            }

            _o.Write("\">Newer posts</a>"u8);
        }

        // The last page Resolve accepts links no further: a link to the next would resolve as a guessed URL.
        if (page < HoneypotSite.MaxIndexPage)
        {
            _o.Write("<a href=\""u8);
            _o.Write(_site.PrefixBytes);
            _o.Write("page/"u8);
            _o.WriteInt(page + 1);
            _o.Write("/\">Older posts</a>"u8);
        }

        _o.Write("</nav>"u8);
        End();
        return Done(null);
    }

    private HoneypotRender RenderListing()
    {
        var name = _kind switch
        {
            HoneypotPageKind.Category => _site.CategoryNames[_local],
            HoneypotPageKind.Tag => _site.TagNames[_local],
            _ => _site.AuthorNames[_local],
        };
        Begin(name, default, withSite: true);
        _o.Write("<h1>"u8);
        if (_kind == HoneypotPageKind.Tag)
        {
            _o.Write("Tagged: "u8);
        }

        _o.Write(name);
        _o.Write("</h1>"u8);
        if (_kind == HoneypotPageKind.Author)
        {
            _o.Write("<p>"u8);
            TextGen.Paragraph(ref _o, ref _rng, 2);
            _o.Write("</p>"u8);
        }
        else
        {
            _o.Write("<p class=\"meta\">"u8);
            TextGen.Sentence(ref _o, ref _rng);
            _o.Write("</p>"u8);
        }

        Cards(CardsPerListing);
        End();
        return Done(null);
    }

    private HoneypotRender RenderNotFound()
    {
        Begin("Page not found"u8, default, withSite: true);
        _o.Write("<h1>Page not found</h1><figure>"u8);
        PostImage(NextId());
        _o.Write("</figure><p>The page is gone, or it never existed. These might help.</p><ul>"u8);
        for (var i = 0; i < 6; i++)
        {
            _o.Write("<li>"u8);
            PostLink(HoneypotLinkKind.Normal, NextId(), default);
            _o.Write("</li>"u8);
        }

        _o.Write("</ul>"u8);
        End();
        return Done(null, status: 404);
    }

    private HoneypotRender RenderPost()
    {
        Span<byte> titleBuffer = stackalloc byte[PostFacts.TitleMax];
        var facts = PostFacts.Create(_site, _kind, _local, titleBuffer);
        var title = titleBuffer[..facts.TitleLength];
        _rng = facts.Rng;

        // The description is the post's first sentence, from a copy of the generator, which the body then draws again from the same state.
        Span<byte> description = stackalloc byte[200];
        var d = new Utf8Out(description);
        var peek = facts.Rng;
        TextGen.Sentence(ref d, ref peek);
        Begin(title, description[..d.Length], withSite: true);

        _o.Write("<article><header><h1>"u8);
        _o.Write(title);
        _o.Write("</h1><p class=\"meta\">"u8);
        MetaLine(in facts, withReadingTime: true);
        _o.Write("</p></header><figure>"u8);
        ImageTag(_local, title);
        _o.Write("</figure>"u8);
        Body();
        _o.Write("<p class=\"tags\">Tagged: "u8);
        for (var i = 0; i < facts.TagCount; i++)
        {
            NamedLink(HoneypotLinkKind.Normal, HoneypotPageKind.Tag, facts.Tag(i));
            _o.Write((byte)' ');
        }

        _o.Write("</p></article><nav class=\"pager\">"u8);
        PostLink(HoneypotLinkKind.Normal, NextId(), "Previous post"u8);
        PostLink(HoneypotLinkKind.Normal, NextId(), "Next post"u8);
        _o.Write("</nav><section><h2>Related posts</h2><ul>"u8);
        for (var i = 0; i < 4; i++)
        {
            _o.Write("<li>"u8);
            PostLink(HoneypotLinkKind.Normal, NextId(), default);
            _o.Write("</li>"u8);
        }

        _o.Write("</ul></section><section><h2>"u8);
        _o.WriteInt(facts.Comments);
        _o.Write(" comments</h2><p>Comments on this post are closed.</p></section>"u8);
        End();
        return Done(new DateTimeOffset(facts.Date, TimeSpan.Zero));
    }

    private HoneypotRender RenderSitemap()
    {
        _o.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n<url><loc>"u8);
        _o.Write(_site.PrefixBytes);
        _o.Write("</loc></url>\n"u8);
        for (var i = 0; i < _site.CategoryNames.Length; i++)
        {
            SitemapEntryNamed(HoneypotPageKind.Category, i);
        }

        for (var i = 0; i < _site.TagNames.Length; i++)
        {
            SitemapEntryNamed(HoneypotPageKind.Tag, i);
        }

        for (var i = 0; i < _site.AuthorNames.Length; i++)
        {
            SitemapEntryNamed(HoneypotPageKind.Author, i);
        }

        for (var i = 0; i < 40; i++)
        {
            SitemapPost(NextId());
        }

        _o.Write("</urlset>\n"u8);
        return Done(null, "application/xml; charset=utf-8");
    }

    private HoneypotRender RenderFeed()
    {
        _o.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<rss version=\"2.0\"><channel><title>"u8);
        _o.Write(_site.NameBytes);
        _o.Write("</title><link>"u8);
        _o.Write(_site.PrefixBytes);
        _o.Write("</link><description>"u8);
        TextGen.Sentence(ref _o, ref _rng);
        _o.Write("</description><language>en</language>\n"u8);
        for (var i = 0; i < 20; i++)
        {
            FeedItem(NextId());
        }

        _o.Write("</channel></rss>\n"u8);
        return Done(null, "application/rss+xml; charset=utf-8");
    }

    private void SitemapPost(uint id)
    {
        Span<byte> title = stackalloc byte[PostFacts.TitleMax];
        var facts = PostFacts.Create(_site, HoneypotPageKind.Post, id, title);
        _o.Write("<url><loc>"u8);
        PostHref(HoneypotLinkKind.Sitemap, id, title[..facts.TitleLength], facts.Date);
        _o.Write("</loc><lastmod>"u8);
        IsoDate(facts.Date);
        _o.Write("</lastmod></url>\n"u8);
    }

    private void FeedItem(uint id)
    {
        Span<byte> title = stackalloc byte[PostFacts.TitleMax];
        var facts = PostFacts.Create(_site, HoneypotPageKind.Post, id, title);
        var t = title[..facts.TitleLength];
        _o.Write("<item><title>"u8);
        _o.Write(t);
        _o.Write("</title><link>"u8);
        PostHref(HoneypotLinkKind.Feed, id, t, facts.Date);
        _o.Write("</link><guid isPermaLink=\"true\">"u8);
        PostHref(HoneypotLinkKind.Feed, id, t, facts.Date);
        _o.Write("</guid><pubDate>"u8);
        Rfc822Date(facts.Date);
        _o.Write("</pubDate><category>"u8);
        _o.Write(_site.CategoryNames[facts.Category]);
        _o.Write("</category><description>"u8);
        var excerpt = facts.Rng;
        TextGen.Paragraph(ref _o, ref excerpt, 2);
        _o.Write("</description></item>\n"u8);
    }

    private HoneypotRender RenderBeacon()
    {
        _o.Write("{}"u8);
        return Done(null, "application/json");
    }

    private HoneypotRender RenderImage()
    {
        var hue = _rng.Below(360);
        var hue2 = (hue + _rng.Between(30, 140)) % 360;
        _o.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 640 360\" width=\"640\" height=\"360\">"u8);
        _o.Write("<defs><linearGradient id=\"g\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\"><stop offset=\"0\" stop-color=\"#"u8);
        _o.WriteHex(Theming.Hsl(hue, 55, 62), 6);
        _o.Write("\"/><stop offset=\"1\" stop-color=\"#"u8);
        _o.WriteHex(Theming.Hsl(hue2, 50, 38), 6);
        _o.Write("\"/></linearGradient>"u8);
        var stripes = _rng.Chance(25);
        if (stripes)
        {
            _o.Write("<pattern id=\"s\" width=\"14\" height=\"14\" patternUnits=\"userSpaceOnUse\" patternTransform=\"rotate(45)\"><rect width=\"5\" height=\"14\" fill=\"#ffffff\" fill-opacity=\".14\"/></pattern>"u8);
        }

        _o.Write("</defs><rect width=\"640\" height=\"360\" fill=\"url(#g)\"/>"u8);
        var shapes = _rng.Between(5, 9);
        for (var i = 0; i < shapes; i++)
        {
            var color = Theming.Hsl((_rng.Chance(50) ? hue : hue2) + _rng.Between(-20, 20) + 360, _rng.Between(35, 70), _rng.Between(35, 85));
            var opacity = _rng.Between(18, 60);
            switch (_rng.Below(3))
            {
                case 0:
                    _o.Write("<circle cx=\""u8);
                    _o.WriteInt(_rng.Between(0, 640));
                    _o.Write("\" cy=\""u8);
                    _o.WriteInt(_rng.Between(0, 360));
                    _o.Write("\" r=\""u8);
                    _o.WriteInt(_rng.Between(20, 110));
                    break;
                case 1:
                    _o.Write("<rect x=\""u8);
                    _o.WriteInt(_rng.Between(0, 560));
                    _o.Write("\" y=\""u8);
                    _o.WriteInt(_rng.Between(0, 300));
                    _o.Write("\" width=\""u8);
                    _o.WriteInt(_rng.Between(40, 220));
                    _o.Write("\" height=\""u8);
                    _o.WriteInt(_rng.Between(30, 160));
                    _o.Write("\" rx=\""u8);
                    _o.WriteInt(_rng.Between(0, 24));
                    break;
                default:
                    _o.Write("<polygon points=\""u8);
                    for (var p = 0; p < 3; p++)
                    {
                        _o.WriteInt(_rng.Between(0, 640));
                        _o.Write((byte)',');
                        _o.WriteInt(_rng.Between(0, 360));
                        _o.Write((byte)' ');
                    }

                    break;
            }

            _o.Write("\" fill=\"#"u8);
            _o.WriteHex(color, 6);
            _o.Write("\" fill-opacity=\"."u8);
            _o.WriteInt(opacity, 2);
            _o.Write("\"/>"u8);
        }

        if (stripes)
        {
            _o.Write("<rect width=\"640\" height=\"360\" fill=\"url(#s)\"/>"u8);
        }

        _o.Write("</svg>"u8);
        return Done(null, "image/svg+xml");
    }
}
