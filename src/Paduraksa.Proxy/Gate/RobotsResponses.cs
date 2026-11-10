using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Model;

namespace Paduraksa.Proxy.Gate;

/// <summary>
/// The compiled answers for the managed <c>robots.txt</c> modes. The two presets are built and encoded once for the
/// process, so every host that selects one shares the same bytes and headers; a custom text is encoded per host at
/// snapshot compile time.
/// </summary>
internal static class RobotsResponses
{
    public static PrecomputedResponse DisallowAll { get; } = For(RobotsText.DisallowAll);

    public static PrecomputedResponse BlockAiCrawlers { get; } = For(RobotsText.BlockAiCrawlers);

    /// <summary>The answer a custom text gets: line endings normalised and a trailing newline ensured.</summary>
    public static PrecomputedResponse Custom(string text) => For(RobotsText.NormalizeCustom(text));

    private static PrecomputedResponse For(string text) =>
        PrecomputedResponse.Create(
            StatusCodes.Status200OK,
            System.Text.Encoding.UTF8.GetBytes(text),

            // An hour lets a crawler re-read the file soon after a change without fetching it on every visit.
            "text/plain; charset=utf-8",
            "max-age=3600");
}
