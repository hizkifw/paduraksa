using Paduraksa.Contracts.Config;
using Paduraksa.Proxy.Net;

namespace Paduraksa.Application.Cloudflare;

/// <summary>What the dashboard and the API may say about the list of Cloudflare's edge ranges in force: a public-facts view of
/// <see cref="CloudflareEdgeRangeSource"/>, which only Proxy and the refresh job see.</summary>
public sealed class CloudflareRangeReport(CloudflareEdgeRangeSource source)
{
    /// <summary>The list in force now.</summary>
    public CloudflareRangeStatus Current
    {
        get
        {
            var status = source.Status();
            return new CloudflareRangeStatus(
                status.List.Origin == EdgeRangeOrigin.BuiltIn ? CloudflareRangeSource.BuiltIn : CloudflareRangeSource.Fetched,
                status.List.FetchedAt,
                status.List.Count,
                status.ConsecutiveFailures,
                status.Warning);
        }
    }
}
