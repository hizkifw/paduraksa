namespace Paduraksa.Proxy.Honeypot;

/// <summary>Links, cards and the post body.</summary>
internal ref partial struct PageRenderer
{
    private uint NextId() => _rng.NextUInt32() & SiteToken.MaxLocal;

    // ---- links ----

    /// <summary>The kind the token records. A noindex page's links all say so (that is the point of the page); an image and a beacon are resources, and stay what they are.</summary>
    private readonly HoneypotLinkKind Recorded(HoneypotLinkKind presentation) =>
        _noIndex && presentation is not (HoneypotLinkKind.Image or HoneypotLinkKind.Beacon) ? HoneypotLinkKind.NoIndexPage : presentation;

    /// <summary>
    /// Writes <c>&lt;prefix&gt;&lt;tail&gt;-&lt;token&gt;&lt;suffix&gt;</c>. With <paramref name="script"/>, the prefix is cut from the rest by <c>"+"</c>, for a
    /// JavaScript string that builds the address.
    /// </summary>
    private void WriteUrl(HoneypotPageKind kind, HoneypotLinkKind presentation, uint local, scoped ReadOnlySpan<byte> tail, scoped ReadOnlySpan<byte> suffix, bool script = false)
    {
        _o.Write(_site.PrefixBytes);
        if (script)
        {
            _o.Write("\"+\""u8);
        }

        _o.Write(tail);
        _o.Write((byte)'-');
        Span<byte> token = stackalloc byte[SiteToken.Length];
        SiteToken.Encode(
            _site.Key,
            _site.TokenSalt,
            kind,
            Recorded(presentation),
            local,
            (HoneypotPageKind)(_self >> 28),
            (uint)(_self & SiteToken.MaxLocal),
            tail,
            token);
        _o.Write(token);
        _o.Write(suffix);
    }

    /// <summary>The attributes that make the link look like its kind, closing the tag.</summary>
    private void AnchorEnd(HoneypotLinkKind presentation) => _o.Write(presentation switch
    {
        HoneypotLinkKind.NoFollow => "\" rel=\"nofollow\">"u8,
        HoneypotLinkKind.Ugc => "\" rel=\"ugc nofollow\">"u8,
        HoneypotLinkKind.Sponsored => "\" rel=\"sponsored nofollow\">"u8,
        HoneypotLinkKind.Hidden => "\" class=\"hd\" tabindex=\"-1\" aria-hidden=\"true\">"u8,
        _ => "\">"u8,
    });

    private static int PostTail(scoped Span<byte> tail, DateTime date, scoped ReadOnlySpan<byte> title)
    {
        var o = new Utf8Out(tail);
        o.WriteInt(date.Year, 4);
        o.Write((byte)'/');
        o.WriteInt(date.Month, 2);
        o.Write((byte)'/');
        Span<byte> slug = stackalloc byte[PostFacts.TitleMax];
        o.Write(slug[..PostFacts.Slugify(title, slug)]);
        return o.Length;
    }

    /// <summary>A post's address, without the tag around it.</summary>
    private void PostHref(HoneypotLinkKind presentation, uint id, scoped ReadOnlySpan<byte> title, DateTime date, bool script = false)
    {
        Span<byte> tail = stackalloc byte[SiteToken.MaxTail];
        WriteUrl(HoneypotPageKind.Post, presentation, id, tail[..PostTail(tail, date, title)], "/"u8, script);
    }

    private void PostHrefFor(HoneypotLinkKind presentation, uint id)
    {
        Span<byte> title = stackalloc byte[PostFacts.TitleMax];
        var facts = PostFacts.Create(_site, HoneypotPageKind.Post, id, title);
        PostHref(presentation, id, title[..facts.TitleLength], facts.Date);
    }

    /// <summary>A link to a post, labelled with its title unless <paramref name="label"/> is given.</summary>
    private void PostLink(HoneypotLinkKind presentation, uint id, scoped ReadOnlySpan<byte> label)
    {
        Span<byte> title = stackalloc byte[PostFacts.TitleMax];
        var facts = PostFacts.Create(_site, HoneypotPageKind.Post, id, title);
        PostAnchor(presentation, id, title[..facts.TitleLength], facts.Date, label);
    }

    private void PostAnchor(HoneypotLinkKind presentation, uint id, scoped ReadOnlySpan<byte> title, DateTime date, scoped ReadOnlySpan<byte> label)
    {
        _o.Write("<a href=\""u8);
        PostHref(presentation, id, title, date);
        AnchorEnd(presentation);
        _o.Write(label.IsEmpty ? title : label);
        _o.Write("</a>"u8);
    }

    private void NamedLink(HoneypotLinkKind presentation, HoneypotPageKind kind, int index, scoped ReadOnlySpan<byte> label = default)
    {
        Span<byte> tail = stackalloc byte[SiteToken.MaxTail];
        var t = new Utf8Out(tail);
        t.Write(Folder(kind));
        t.Write(SlugOf(kind, index));
        _o.Write("<a href=\""u8);
        WriteUrl(kind, presentation, (uint)index, tail[..t.Length], "/"u8);
        AnchorEnd(presentation);
        _o.Write(label.IsEmpty ? NameOf(kind, index) : label);
        _o.Write("</a>"u8);
    }

    private static ReadOnlySpan<byte> Folder(HoneypotPageKind kind) => kind switch
    {
        HoneypotPageKind.Category => "category/"u8,
        HoneypotPageKind.Tag => "tag/"u8,
        _ => "author/"u8,
    };

    private readonly byte[] SlugOf(HoneypotPageKind kind, int index) => kind switch
    {
        HoneypotPageKind.Category => _site.CategorySlugs[index],
        HoneypotPageKind.Tag => _site.TagSlugs[index],
        _ => _site.AuthorSlugs[index],
    };

    private readonly byte[] NameOf(HoneypotPageKind kind, int index) => kind switch
    {
        HoneypotPageKind.Category => _site.CategoryNames[index],
        HoneypotPageKind.Tag => _site.TagNames[index],
        _ => _site.AuthorNames[index],
    };

    private void SitemapEntryNamed(HoneypotPageKind kind, int index)
    {
        Span<byte> tail = stackalloc byte[SiteToken.MaxTail];
        var t = new Utf8Out(tail);
        t.Write(Folder(kind));
        t.Write(SlugOf(kind, index));
        _o.Write("<url><loc>"u8);
        WriteUrl(kind, HoneypotLinkKind.Sitemap, (uint)index, tail[..t.Length], "/"u8);
        _o.Write("</loc></url>\n"u8);
    }

    private void PostImage(uint postId)
    {
        Span<byte> title = stackalloc byte[PostFacts.TitleMax];
        var facts = PostFacts.Create(_site, HoneypotPageKind.Post, postId, title);
        ImageTag(postId, title[..facts.TitleLength]);
    }

    private void ImageTag(uint postId, scoped ReadOnlySpan<byte> title)
    {
        Span<byte> tail = stackalloc byte[SiteToken.MaxTail];
        var t = new Utf8Out(tail);
        t.Write("media/"u8);
        Span<byte> slug = stackalloc byte[PostFacts.TitleMax];
        t.Write(slug[..PostFacts.Slugify(title, slug)]);
        _o.Write("<img src=\""u8);
        WriteUrl(HoneypotPageKind.Image, HoneypotLinkKind.Image, postId, tail[..t.Length], ".svg"u8);
        _o.Write("\" alt=\"\" width=\"640\" height=\"360\" loading=\"lazy\">"u8);
    }

    // ---- listings ----

    private void Cards(int count)
    {
        for (var i = 0; i < count; i++)
        {
            Card(NextId());
        }
    }

    private void Card(uint id)
    {
        Span<byte> titleBuffer = stackalloc byte[PostFacts.TitleMax];
        var facts = PostFacts.Create(_site, HoneypotPageKind.Post, id, titleBuffer);
        var title = titleBuffer[..facts.TitleLength];
        _o.Write("<article class=\"card\">"u8);
        ImageTag(id, title);
        _o.Write("<h2>"u8);
        PostAnchor(HoneypotLinkKind.Normal, id, title, facts.Date, default);
        _o.Write("</h2><p class=\"meta\">"u8);
        MetaLine(in facts, withReadingTime: false);
        _o.Write("</p><p>"u8);
        var excerpt = facts.Rng;
        TextGen.Sentence(ref _o, ref excerpt);
        _o.Write((byte)' ');
        PostAnchor(HoneypotLinkKind.Normal, id, title, facts.Date, "Continue reading"u8);
        _o.Write("</p></article>"u8);
    }

    private void MetaLine(scoped in PostFacts facts, bool withReadingTime)
    {
        _o.Write("<time datetime=\""u8);
        IsoDate(facts.Date);
        _o.Write("\">"u8);
        _o.Write(MonthName(facts.Date.Month));
        _o.Write((byte)' ');
        _o.WriteInt(facts.Date.Day);
        _o.Write(", "u8);
        _o.WriteInt(facts.Date.Year);
        _o.Write("</time> by "u8);
        NamedLink(HoneypotLinkKind.Normal, HoneypotPageKind.Author, facts.Author);
        _o.Write(" in "u8);
        NamedLink(HoneypotLinkKind.Normal, HoneypotPageKind.Category, facts.Category);
        if (withReadingTime)
        {
            _o.Write(", "u8);
            _o.WriteInt(facts.ReadMinutes);
            _o.Write(" min read, "u8);
        }
        else
        {
            _o.Write(", "u8);
        }

        _o.WriteInt(facts.Comments);
        _o.Write(" comments"u8);
    }

    // ---- the post body ----

    private void Body()
    {
        var paragraph = 0;
        Paragraph(ref paragraph, link: false);
        var sections = _rng.Between(3, 5);
        for (var s = 0; s < sections; s++)
        {
            _o.Write("<h2>"u8);
            TextGen.Heading(ref _o, ref _rng);
            _o.Write("</h2>"u8);
            Paragraph(ref paragraph, _rng.Chance(60));
            if (_rng.Chance(50))
            {
                Paragraph(ref paragraph, _rng.Chance(40));
            }

            switch (_rng.Below(5))
            {
                case 0:
                    List("ul"u8);
                    break;
                case 1:
                    List("ol"u8);
                    break;
                case 2:
                    _o.Write("<blockquote><p>&ldquo;"u8);
                    TextGen.Sentence(ref _o, ref _rng);
                    _o.Write("&rdquo;</p><footer>"u8);
                    _o.Write(_site.AuthorNames[_rng.Below(_site.AuthorNames.Length)]);
                    _o.Write("</footer></blockquote>"u8);
                    break;
                case 3:
                    _o.Write("<pre><code>"u8);
                    TextGen.CodeLines(ref _o, ref _rng);
                    _o.Write("</code></pre>"u8);
                    break;
                default:
                    break;
            }
        }
    }

    private void Paragraph(ref int index, bool link)
    {
        _o.Write("<p>"u8);
        TextGen.Paragraph(ref _o, ref _rng, _rng.Between(3, 5));
        if (link)
        {
            _o.Write((byte)' ');
            TextGen.LinkLead(ref _o, ref _rng);
            PostLink(HoneypotLinkKind.Normal, NextId(), default);
            _o.Write((byte)'.');
        }

        // The phrase goes in as one more sentence, and draws nothing, so the rest of the page is the same for every visitor.
        if (index == 1 + (int)(_local % 3) && !_canary.IsEmpty)
        {
            _o.Write((byte)' ');
            _o.Write(((_local >> 2) % 4) switch
            {
                0 => "Some readers call it the "u8,
                1 => "A friend of mine calls this "u8,
                2 => "In the old notes it was simply known as "u8,
                _ => "People keep asking about "u8,
            });
            _o.Write(_canary);
            _o.Write(((_local >> 2) % 4) switch
            {
                0 => " method."u8,
                1 => ", and the name stuck."u8,
                2 => "."u8,
                _ => ", so here it is."u8,
            });
        }

        _o.Write("</p>"u8);
        index++;
    }

    private void List(scoped ReadOnlySpan<byte> tag)
    {
        _o.Write((byte)'<');
        _o.Write(tag);
        _o.Write((byte)'>');
        var items = _rng.Between(3, 5);
        for (var i = 0; i < items; i++)
        {
            _o.Write("<li>"u8);
            TextGen.ListItem(ref _o, ref _rng);
            _o.Write("</li>"u8);
        }

        _o.Write("</"u8);
        _o.Write(tag);
        _o.Write((byte)'>');
    }

    // ---- dates ----

    private static ReadOnlySpan<byte> MonthName(int month) => month switch
    {
        1 => "January"u8,
        2 => "February"u8,
        3 => "March"u8,
        4 => "April"u8,
        5 => "May"u8,
        6 => "June"u8,
        7 => "July"u8,
        8 => "August"u8,
        9 => "September"u8,
        10 => "October"u8,
        11 => "November"u8,
        _ => "December"u8,
    };

    private void IsoDate(DateTime date)
    {
        _o.WriteInt(date.Year, 4);
        _o.Write((byte)'-');
        _o.WriteInt(date.Month, 2);
        _o.Write((byte)'-');
        _o.WriteInt(date.Day, 2);
    }

    private void Rfc822Date(DateTime date)
    {
        _o.Write(date.DayOfWeek switch
        {
            DayOfWeek.Monday => "Mon"u8,
            DayOfWeek.Tuesday => "Tue"u8,
            DayOfWeek.Wednesday => "Wed"u8,
            DayOfWeek.Thursday => "Thu"u8,
            DayOfWeek.Friday => "Fri"u8,
            DayOfWeek.Saturday => "Sat"u8,
            _ => "Sun"u8,
        });
        _o.Write(", "u8);
        _o.WriteInt(date.Day, 2);
        _o.Write((byte)' ');
        _o.Write(MonthName(date.Month)[..3]);
        _o.Write((byte)' ');
        _o.WriteInt(date.Year, 4);
        _o.Write((byte)' ');
        _o.WriteInt(date.Hour, 2);
        _o.Write((byte)':');
        _o.WriteInt(date.Minute, 2);
        _o.Write((byte)':');
        _o.WriteInt(date.Second, 2);
        _o.Write(" +0000"u8);
    }
}
