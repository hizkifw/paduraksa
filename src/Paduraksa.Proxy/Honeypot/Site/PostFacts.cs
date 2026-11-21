namespace Paduraksa.Proxy.Honeypot;

/// <summary>
/// What a page of the post kind is: title, date, author, category, tags, counts. Derived from the post's own generator, in a fixed order,
/// so a link to a post and the post itself always agree. <see cref="Rng"/> is left where the body starts, so a card's excerpt is the first
/// sentence of the post.
/// </summary>
internal ref struct PostFacts
{
    /// <summary>The longest title kept; the templates stay far below it.</summary>
    public const int TitleMax = 96;

    // Posts are spread over about five and a half years; the window is fixed because a page can't depend on the clock.
    private const int SpanDays = 2000;
    private static readonly DateTime Epoch = new(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public SiteRandom Rng;
    public int TitleLength;
    public DateTime Date;
    public int Author;
    public int Category;
    public int TagCount;
    public uint Tags;
    public int ReadMinutes;
    public int Comments;

    public static PostFacts Create(HoneypotSite site, HoneypotPageKind kind, uint local, scoped Span<byte> title)
    {
        var facts = default(PostFacts);
        facts.Rng = SiteKeys.PageRandom(site.Key, kind, local);
        var o = new Utf8Out(title);
        TextGen.Title(ref o, ref facts.Rng);
        facts.TitleLength = o.Length;
        facts.Date = Epoch.AddDays(facts.Rng.Below(SpanDays)).AddSeconds(facts.Rng.Below(86400));
        facts.Author = facts.Rng.Below(site.AuthorNames.Length);
        facts.Category = facts.Rng.Below(site.CategoryNames.Length);
        facts.TagCount = facts.Rng.Between(2, 4);
        for (var i = 0; i < facts.TagCount; i++)
        {
            int tag;
            do
            {
                tag = facts.Rng.Below(site.TagNames.Length);
            }
            while (facts.HasTag(tag, i));

            facts.Tags |= (uint)tag << (i * 8);
        }

        facts.ReadMinutes = facts.Rng.Between(3, 14);
        facts.Comments = facts.Rng.Between(2, 40);
        return facts;
    }

    public readonly int Tag(int index) => (int)((Tags >> (index * 8)) & 0xFF);

    /// <summary>The URL slug of a title: lower-case letters and digits, one hyphen between words.</summary>
    public static int Slugify(ReadOnlySpan<byte> title, Span<byte> slug)
    {
        var length = 0;
        foreach (var b in title)
        {
            if (length == slug.Length)
            {
                break;
            }

            if (b is >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9')
            {
                slug[length++] = b;
            }
            else if (b is >= (byte)'A' and <= (byte)'Z')
            {
                slug[length++] = (byte)(b + 32);
            }
            else if (length > 0 && slug[length - 1] != (byte)'-')
            {
                slug[length++] = (byte)'-';
            }
        }

        while (length > 0 && slug[length - 1] == (byte)'-')
        {
            length--;
        }

        return length;
    }

    private readonly bool HasTag(int tag, int taken)
    {
        for (var i = 0; i < taken; i++)
        {
            if (Tag(i) == tag)
            {
                return true;
            }
        }

        return false;
    }
}
