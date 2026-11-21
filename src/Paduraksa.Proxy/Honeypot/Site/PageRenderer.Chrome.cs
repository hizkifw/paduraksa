namespace Paduraksa.Proxy.Honeypot;

/// <summary>The parts every HTML page shares: head, header, sidebar, footer and the inline script.</summary>
internal ref partial struct PageRenderer
{
    private const int HiddenLinks = 2;

    private void Begin(scoped ReadOnlySpan<byte> title, scoped ReadOnlySpan<byte> description, bool withSite)
    {
        _o.Write("<!DOCTYPE html>\n<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>"u8);
        _o.Write(title);
        if (withSite)
        {
            _o.Write(" | "u8);
            _o.Write(_site.NameBytes);
        }

        _o.Write("</title>"u8);
        if (_noIndex)
        {
            _o.Write("<meta name=\"robots\" content=\"noindex, nofollow\">"u8);
        }

        if (!description.IsEmpty)
        {
            _o.Write("<meta name=\"description\" content=\""u8);
            _o.Write(description);
            _o.Write("\">"u8);
        }

        _o.Write("<link rel=\"alternate\" type=\"application/rss+xml\" title=\""u8);
        _o.Write(_site.NameBytes);
        _o.Write("\" href=\""u8);
        _o.Write(_site.PrefixBytes);
        _o.Write("feed/\">"u8);
        _o.Write(_site.Theme);
        _o.Write("</head><body><header class=\"top\"><div class=\"bar\"><a class=\"brand\" href=\""u8);
        _o.Write(_site.PrefixBytes);
        _o.Write("\">"u8);
        _o.Write(_site.NameBytes);
        _o.Write("</a><nav><a href=\""u8);
        _o.Write(_site.PrefixBytes);
        _o.Write("\">Home</a> "u8);
        for (var i = 0; i < 4; i++)
        {
            NamedLink(HoneypotLinkKind.Normal, HoneypotPageKind.Category, i);
            _o.Write((byte)' ');
        }

        NamedLink(HoneypotLinkKind.Normal, HoneypotPageKind.Author, 0, "About"u8);
        _o.Write("</nav></div></header><div class=\"wrap\"><main>"u8);
    }

    private void End()
    {
        _o.Write("</main><aside><section><h3>Categories</h3><ul>"u8);
        for (var i = 0; i < _site.CategoryNames.Length; i++)
        {
            _o.Write("<li>"u8);
            NamedLink(HoneypotLinkKind.Normal, HoneypotPageKind.Category, i);
            _o.Write("</li>"u8);
        }

        _o.Write("</ul></section><section><h3>Tags</h3><p class=\"tags\">"u8);
        var firstTag = _rng.Below(_site.TagNames.Length);
        for (var i = 0; i < 10; i++)
        {
            NamedLink(HoneypotLinkKind.Normal, HoneypotPageKind.Tag, (firstTag + i) % _site.TagNames.Length);
            _o.Write((byte)' ');
        }

        _o.Write("</p></section><section><h3>Popular</h3><ol>"u8);
        for (var i = 0; i < 3; i++)
        {
            _o.Write("<li>"u8);
            PostLink(HoneypotLinkKind.NoFollow, NextId(), default);
            _o.Write("</li>"u8);
        }

        _o.Write("</ol></section><section><h3>Recent comments</h3><ul>"u8);
        for (var i = 0; i < 2; i++)
        {
            _o.Write("<li>"u8);
            _o.Write(Lexicon.FirstNames[_rng.Below(Lexicon.FirstNames.Length)]);
            _o.Write(" on "u8);
            PostLink(HoneypotLinkKind.Ugc, NextId(), default);
            _o.Write("</li>"u8);
        }

        _o.Write("</ul></section><section><h3>Sponsored</h3><p>"u8);
        PostLink(HoneypotLinkKind.Sponsored, NextId(), default);
        _o.Write("</p></section></aside></div><footer class=\"end\"><p>"u8);
        _o.Write(_site.NameBytes);
        _o.Write(" <a href=\""u8);
        _o.Write(_site.PrefixBytes);
        _o.Write("feed/\">Feed</a> <a href=\""u8);
        _o.Write(_site.PrefixBytes);
        _o.Write("sitemap.xml\">Sitemap</a></p>"u8);
        for (var i = 0; i < HiddenLinks; i++)
        {
            PostLink(HoneypotLinkKind.Hidden, NextId(), default);
        }

        _o.Write("<!-- "u8);
        PostHrefFor(HoneypotLinkKind.Comment, NextId());
        _o.Write(" --><ul id=\"more\"></ul></footer>"u8);
        Script();
        _o.Write("</body></html>\n"u8);
    }

    /// <summary>
    /// The one inline script: it adds a link that exists only once script has run, and fetches the beacon. A crawler that follows the
    /// link or fetches the beacon without ever clicking has run the script.
    /// </summary>
    private void Script()
    {
        Span<byte> title = stackalloc byte[PostFacts.TitleMax];
        var id = NextId();
        var facts = PostFacts.Create(_site, HoneypotPageKind.Post, id, title);
        _o.Write("<script>(function(){var d=document,m=d.getElementById(\"more\"),a=d.createElement(\"a\"),l=d.createElement(\"li\");a.href=\""u8);
        PostHref(HoneypotLinkKind.Script, id, title[..facts.TitleLength], facts.Date, script: true);
        _o.Write("\";a.textContent=\""u8);
        _o.Write(title[..facts.TitleLength]);
        _o.Write("\";l.appendChild(a);m.appendChild(l);fetch(\""u8);
        BeaconUrl();
        _o.Write("\",{credentials:\"omit\"}).catch(function(){})})();</script>"u8);
    }

    private void BeaconUrl()
    {
        WriteUrl(HoneypotPageKind.Beacon, HoneypotLinkKind.Beacon, NextId(), "assets/stats"u8, ".json"u8, script: true);
    }

    private HoneypotRender Done(DateTimeOffset? lastModified, string contentType = "text/html; charset=utf-8", int status = 200)
    {
        _o.Flush();
        return new HoneypotRender(status, contentType, lastModified);
    }
}
