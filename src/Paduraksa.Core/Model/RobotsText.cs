using System.Text;

namespace Paduraksa.Core.Model;

/// <summary>
/// The managed <c>robots.txt</c> texts, shared by the proxy that serves them and the dashboard that previews them, so both
/// say the same thing. The AI crawler list is part of the build, not a setting: it goes stale as crawlers appear, and is
/// refreshed with each Paduraksa release rather than by an operator who would have to notice.
/// </summary>
public static class RobotsText
{
    /// <summary>The longest custom text, in UTF-8 bytes (32 KiB).</summary>
    public const int MaxCustomBytes = 32 * 1024;

    /// <summary>Disallow every crawler.</summary>
    public const string DisallowAll = "User-agent: *\nDisallow: /\n";

    /// <summary>
    /// The crawlers <see cref="RobotsMode.BlockAiCrawlers"/> disallows, with the casing their operators publish. Their
    /// tokens are matched case-insensitively by well-behaved crawlers, but a list that matches the documentation is the
    /// one an operator can check.
    /// </summary>
    public static IReadOnlyList<string> AiCrawlers { get; } = Array.AsReadOnly<string>(
    [
        // OpenAI.
        "GPTBot", "ChatGPT-User", "OAI-SearchBot",

        // Anthropic.
        "ClaudeBot", "Claude-User", "Claude-SearchBot", "anthropic-ai", "Claude-Web",

        // Common Crawl, Google's and Apple's model-training crawlers.
        "CCBot", "Google-Extended", "Applebot-Extended",

        // Perplexity.
        "PerplexityBot", "Perplexity-User",

        // ByteDance and Amazon.
        "Bytespider", "Amazonbot",

        // Meta.
        "Meta-ExternalAgent", "Meta-ExternalFetcher", "Meta-WebIndexer", "FacebookBot",

        // Cohere.
        "cohere-ai", "cohere-training-data-crawler",

        // Diffbot, ImgSift, Omgili, Timpi, You.com.
        "Diffbot", "ImagesiftBot", "omgilibot", "Timpibot", "YouBot",

        // The Allen Institute, DuckDuckGo, Mistral, Pangu, Webz.io and the img2dataset tool.
        "AI2Bot", "Ai2Bot-Dolma", "DuckAssistBot", "MistralAI-User", "PanguBot", "Webzio-Extended", "img2dataset",
    ]);

    /// <summary>
    /// The AI crawlers disallowed and everyone else allowed: one group lists every crawler with <c>Disallow: /</c>, then
    /// <c>User-agent: *</c> with an empty <c>Disallow:</c> lifts the rule for every other crawler.
    /// </summary>
    public static string BlockAiCrawlers { get; } = BuildBlockAiCrawlers();

    /// <summary>
    /// The operator's text in the form the proxy serves it: <c>\r\n</c> and <c>\r</c> become <c>\n</c>, and it ends with a
    /// newline. The stored document keeps the line endings the operator typed; only the compiled bytes are normalised.
    /// </summary>
    public static string NormalizeCustom(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = text.Contains('\r') ? WithoutCarriageReturns(text) : text;
        return normalized.EndsWith('\n') ? normalized : normalized + "\n";
    }

    /// <summary>
    /// True when the text has no control characters other than tab, carriage return and line feed. A <c>robots.txt</c> is
    /// plain text, and a NUL or another control character is either a mistake or an attempt to smuggle bytes into it.
    /// </summary>
    public static bool HasNoControlCharacters(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var c in text)
        {
            if (char.IsControl(c) && c is not ('\t' or '\r' or '\n'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether the text is no longer than <see cref="MaxCustomBytes"/> as UTF-8.</summary>
    public static bool IsWithinSizeLimit(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Encoding.UTF8.GetByteCount(text) <= MaxCustomBytes;
    }

    private static string BuildBlockAiCrawlers()
    {
        // One group for every crawler: consecutive User-agent lines share the Disallow that follows them. The comment is
        // the file's, so an operator who reads it knows why these names are here and where they come from.
        var builder = new StringBuilder("# AI crawlers disallowed; the list is updated with each Paduraksa release.\n");
        foreach (var crawler in AiCrawlers)
        {
            builder.Append("User-agent: ").Append(crawler).Append('\n');
        }

        builder.Append("Disallow: /\n\n");
        builder.Append("User-agent: *\n");
        builder.Append("Disallow:\n");
        return builder.ToString();
    }

    private static string WithoutCarriageReturns(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                builder.Append('\n');
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
