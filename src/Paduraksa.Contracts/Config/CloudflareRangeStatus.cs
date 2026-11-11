namespace Paduraksa.Contracts.Config;

/// <summary>Which list of Cloudflare's edge ranges is in force.</summary>
public enum CloudflareRangeSource
{
    /// <summary>The list this build ships; no refresh has stored a newer one.</summary>
    BuiltIn,

    /// <summary>A list fetched from Cloudflare and stored.</summary>
    Fetched,
}

/// <summary>The list of Cloudflare's edge ranges the proxy trusts in <c>CloudflareProxy</c> mode, and how its refresh is going.</summary>
/// <param name="Source">Which list is in force.</param>
/// <param name="FetchedAt">When it was fetched (for the built-in list, the day this build's copy was).</param>
/// <param name="Count">How many ranges it has.</param>
/// <param name="ConsecutiveFailures">Refreshes in a row that failed since the last good one.</param>
/// <param name="Warning">What is wrong with it (older than 30 days, or the refreshes keep failing); null when it is fine.</param>
public sealed record CloudflareRangeStatus(
    CloudflareRangeSource Source,
    DateTimeOffset FetchedAt,
    int Count,
    int ConsecutiveFailures,
    string? Warning);
