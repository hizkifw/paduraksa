using System.Buffers;
using System.Security.Cryptography;
using System.Text;

using Paduraksa.Proxy.Pages;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.Tests.Proxy.Pages;

/// <summary>The shared layout, its precomputed segments and the encoding of the values a request writes into them.</summary>
public sealed class HtmlPageTemplateTests
{
    private const string Script = "document.title='ok';";

    [Fact]
    public void TheLayoutWrapsTheBodyAndTheSlotLandsBetweenItsSegments()
    {
        var template = HtmlPageTemplate.Create("Page not found", "<h1>Page not found.</h1><code>{requestId}</code>", slots: ["requestId"]);
        var page = Encoding.UTF8.GetString(template.Render("r-1", default, default));

        Assert.StartsWith("<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">", page, StringComparison.Ordinal);
        Assert.Contains("<title>Page not found</title><style>", page, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Paduraksa\"", page, StringComparison.Ordinal);
        Assert.Contains("<main><svg", page, StringComparison.Ordinal);
        Assert.Contains("<h1>Page not found.</h1><code>r-1</code></main>", page, StringComparison.Ordinal);
        Assert.Contains("Protected by Paduraksa</a></footer></body></html>\n", page, StringComparison.Ordinal);
        Assert.Equal(1, template.SlotCount);
    }

    [Fact]
    public void SlotValuesAreHtmlEncodedNotConcatenated()
    {
        var template = HtmlPageTemplate.Create("x", "<p>{requestId}</p>", slots: ["requestId"]);
        var page = Encoding.UTF8.GetString(template.Render("<script>alert('x')</script>&\"", default, default));

        Assert.Contains("<p>&lt;script&gt;alert(&#x27;x&#x27;)&lt;/script&gt;&amp;&quot;</p>", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert", page, StringComparison.Ordinal);
    }

    [Fact]
    public void MultiByteAndSurrogateValuesAreEncodedAsEntities()
    {
        var template = HtmlPageTemplate.Create("x", "<p>{value}</p>", slots: ["value"]);
        var page = Encoding.UTF8.GetString(template.Render("caf\u00E9 \u4E2D \U0001F600", default, default));

        Assert.Contains("<p>caf&#xE9; &#x4E2D; &#x1F600;</p>", page, StringComparison.Ordinal);
    }

    [Fact]
    public void ASurrogatePairAtTheEncoderChunkBoundarySurvivesWhole()
    {
        var template = HtmlPageTemplate.Create("x", "<p>{value}</p>", slots: ["value"]);
        // 250 plain characters leave six chars of the 256-char buffer: the emoji's ten-character entity doesn't fit, so
        // the pair must be carried to the next chunk whole, not split into two replacement characters.
        var value = new string('a', 250) + "\U0001F600z";
        var page = Encoding.UTF8.GetString(template.Render(value, default, default));

        Assert.Contains(new string('a', 250) + "&#x1F600;z", page, StringComparison.Ordinal);
        Assert.DoesNotContain('\uFFFD', page);
    }

    [Fact]
    public void AScriptPageHashesTheScriptItReallyInlines()
    {
        var template = HtmlPageTemplate.Create(
            "Checking", "<p>{first}</p><p>{second}</p>", Script, "worker-src blob:", allowForm: true, slots: ["first", "second"]);
        var page = Encoding.UTF8.GetString(template.Render("a", "b", default));

        Assert.Contains($"script-src '{Hash(Script)}'", template.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.Contains($"style-src '{Hash(HtmlPageTemplate.StyleText)}'", template.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.Contains("worker-src blob:; form-action 'self'; base-uri 'none'; frame-ancestors 'none'", template.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.Contains($"<script>{Script}</script></main>", page, StringComparison.Ordinal);
        Assert.Contains($"<style>{HtmlPageTemplate.StyleText}</style>", page, StringComparison.Ordinal);
    }

    [Fact]
    public void APageWithoutAScriptAllowsNoScriptAndNoForm()
    {
        var template = HtmlPageTemplate.Create("Stopped", "<p>{requestId}</p>", slots: ["requestId"]);

        Assert.Contains($"default-src 'none'; style-src '{Hash(HtmlPageTemplate.StyleText)}'; form-action 'none'; base-uri 'none'; frame-ancestors 'none'", template.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("script-src", template.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("worker-src", template.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", Encoding.UTF8.GetString(template.Render("r", default, default)), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePageCarriesNoReferrerAndDoesNotSniffItsType()
    {
        var template = HtmlPageTemplate.Create("Stopped", "<p>{requestId}</p>", slots: ["requestId"]);
        var headers = template.SecurityHeaders;

        Assert.Contains(headers, header => header.Name == "Referrer-Policy" && header.Value == "no-referrer");
        Assert.Contains(headers, header => header.Name == "X-Content-Type-Options" && header.Value == "nosniff");
        Assert.Contains(headers, header => header.Name == "Content-Security-Policy");
    }

    [Fact]
    public void WritingAPageAllocatesNothing()
    {
        var template = HtmlPageTemplate.Create("Checking", "<p>{first}</p><p>{second}</p>", Script, "worker-src blob:", allowForm: true, slots: ["first", "second"]);
        var state = (Writer: new NullBufferWriter(), Template: template, First: "abc", Second: "/a?b=1&c=2");

        AllocAssert.Zero(
            state,
            static s =>
            {
                s.Template.Write(s.Writer, s.First, s.Second);
            },
            "HtmlPageTemplate.Write");
    }

    [Fact]
    public void ASlotMustAppearOnceAndTheWriteMustMatchTheSlotCount()
    {
        Assert.Throws<ArgumentException>(() => HtmlPageTemplate.Create("x", "<p>{requestId}</p>", slots: ["status"]));
        Assert.Throws<ArgumentException>(() => HtmlPageTemplate.Create("x", "<p>{requestId}{requestId}</p>", slots: ["requestId"]));

        var one = HtmlPageTemplate.Create("x", "<p>{requestId}</p>", slots: ["requestId"]);
        Assert.Throws<InvalidOperationException>(() => one.Write(new NullBufferWriter(), "a", "b"));
    }

    private static string Hash(string content) => "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>A fixed span the encoder writes into; the page's bytes are already asserted above.</summary>
    private sealed class NullBufferWriter : IBufferWriter<byte>
    {
        private readonly byte[] _buffer = new byte[16 * 1024];

        public void Advance(int count)
        {
        }

        public Memory<byte> GetMemory(int sizeHint = 0) => _buffer;

        public Span<byte> GetSpan(int sizeHint = 0) => _buffer;
    }
}
