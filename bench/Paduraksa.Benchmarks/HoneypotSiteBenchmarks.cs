using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

using BenchmarkDotNet.Attributes;

using Paduraksa.Proxy.Honeypot;

namespace Paduraksa.Benchmarks;

/// <summary>
/// The trap site's cost, which only trap traffic pays (the gate's prefix check is the 0 B part): a post (about 10 KB), the index, the sitemap
/// and the feed rendered into a reused buffer, and <see cref="HoneypotSite.Resolve"/> of a valid token, of one with a character of its tag changed (the
/// unmasked fields are usually invalid, so most are refused before any hashing) and of one with a character of its URL text changed (the fields are
/// intact, so the HMAC is computed and fails).
/// </summary>
[MemoryDiagnoser]
public partial class HoneypotSiteBenchmarks
{
    private readonly ArrayBufferWriter<byte> _writer = new(64 * 1024);
    private HoneypotSite _site = null!;
    private HoneypotTarget _post;
    private HoneypotTarget _index;
    private HoneypotTarget _sitemap;
    private HoneypotTarget _feed;
    private HoneypotTarget _category;
    private HoneypotTarget _image;
    private HoneypotTarget _notFound;
    private HoneypotTarget _landing;
    private readonly byte[] _clientIp = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff, 203, 0, 113, 9];
    private string _validPath = null!;
    private string _garbagePath = null!;
    private string _tamperedPath = null!;

    [GlobalSetup]
    public void Setup()
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++)
        {
            key[i] = (byte)((i * 7) + 3);
        }

        var prefix = HoneypotSite.DerivePrefix(key);
        _site = new HoneypotSite(key, new HoneypotSiteOptions(prefix, HoneypotSite.DeriveSiteName(key), 20));
        _index = _site.Resolve(prefix);
        _sitemap = _site.Resolve(prefix + "sitemap.xml");
        _feed = _site.Resolve(prefix + "feed/");
        _writer.Clear();
        _ = _site.Render(_index, string.Empty, _writer);
        var html = Encoding.UTF8.GetString(_writer.WrittenSpan);
        foreach (Match link in Href().Matches(html))
        {
            var target = _site.Resolve(link.Groups[1].Value);
            if (target.Kind == HoneypotPageKind.Post && _validPath is null)
            {
                _post = target;
                _validPath = link.Groups[1].Value;
            }
            else if (target.Kind == HoneypotPageKind.Category && _category.Kind != HoneypotPageKind.Category)
            {
                _category = target;
            }
            else if (target.Kind == HoneypotPageKind.Image && _image.Kind != HoneypotPageKind.Image)
            {
                _image = target;
            }
        }

        _landing = _site.Landing("/wp-login.php");

        _garbagePath = string.Concat(_validPath.AsSpan(0, _validPath.Length - 2), _validPath[^2] == 'a' ? "b" : "a", "/");
        var slugAt = _validPath.LastIndexOf('/', _validPath.Length - 2) + 1;
        _tamperedPath = string.Concat(_validPath.AsSpan(0, slugAt), _validPath[slugAt] == 'a' ? "b" : "a", _validPath.AsSpan(slugAt + 1));
        _notFound = _site.Resolve(_garbagePath);
        if (_post.Kind != HoneypotPageKind.Post
            || _site.Resolve(_validPath) != _post
            || _site.Resolve(_garbagePath).Kind != HoneypotPageKind.NotFound
            || _site.Resolve(_tamperedPath).Kind != HoneypotPageKind.NotFound
            || _category.Kind != HoneypotPageKind.Category
            || _image.Kind != HoneypotPageKind.Image)
        {
            throw new InvalidOperationException("The benchmark's pages did not resolve as expected.");
        }
    }

    [Benchmark(Baseline = true)]
    public HoneypotRender Post() => Render(_post);

    [Benchmark]
    public HoneypotRender Index() => Render(_index);

    [Benchmark]
    public HoneypotRender Sitemap() => Render(_sitemap);

    [Benchmark]
    public HoneypotRender Feed() => Render(_feed);

    [Benchmark]
    public HoneypotRender Category() => Render(_category);

    [Benchmark]
    public HoneypotRender Image() => Render(_image);

    [Benchmark]
    public HoneypotRender NotFound() => Render(_notFound);

    [Benchmark]
    public HoneypotRender Landing() => Render(_landing);

    [Benchmark]
    public string Canary() => _site.Canary(_clientIp, "Mozilla/5.0 (compatible; ExampleBot/2.1; +https://example.test/bot)");

    [Benchmark]
    public HoneypotTarget ResolveValidToken() => _site.Resolve(_validPath);

    [Benchmark]
    public HoneypotTarget ResolveGarbageToken() => _site.Resolve(_garbagePath);

    [Benchmark]
    public HoneypotTarget ResolveTamperedText() => _site.Resolve(_tamperedPath);

    private HoneypotRender Render(HoneypotTarget target)
    {
        _writer.ResetWrittenCount();
        return _site.Render(target, "fox trot", _writer);
    }

    [GeneratedRegex("(?:href|src)=\"([^\"]+)\"")]
    private static partial Regex Href();
}
