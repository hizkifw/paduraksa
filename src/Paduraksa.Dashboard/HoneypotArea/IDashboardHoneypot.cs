using Paduraksa.Application.Honeypot;
using Paduraksa.Contracts.Honeypot;
using Paduraksa.Contracts.Metrics;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.HoneypotArea;

/// <summary>
/// What the honeypot section of Analytics asks of the Application: the same four reads the admin API serves, so the section is as thin
/// over the contracts as a client is. A seam so the section can be tested without a database. Reads are not authorized here, as on the API:
/// the section checks <c>metrics:read</c> first.
/// </summary>
public interface IDashboardHoneypot
{
    Task<HoneypotSummary> ReadSummaryAsync(MetricsRange range, HostId? host, CancellationToken cancellationToken = default);

    Task<HoneypotSessionPage> ListSessionsAsync(HoneypotSessionFilter filter, int limit, HoneypotSessionId? after, CancellationToken cancellationToken = default);

    /// <summary>A page of a session's requests, oldest first; null when the session is gone (past retention).</summary>
    Task<HoneypotRequestPage?> ListRequestsAsync(HoneypotSessionId id, int limit, HoneypotRequestId? after, CancellationToken cancellationToken = default);
}

internal sealed class DashboardHoneypot(HoneypotService honeypot) : IDashboardHoneypot
{
    public Task<HoneypotSummary> ReadSummaryAsync(MetricsRange range, HostId? host, CancellationToken cancellationToken = default) =>
        honeypot.ReadSummaryAsync(range, host, cancellationToken);

    public Task<HoneypotSessionPage> ListSessionsAsync(HoneypotSessionFilter filter, int limit, HoneypotSessionId? after, CancellationToken cancellationToken = default) =>
        honeypot.ListSessionsAsync(filter, limit, after, cancellationToken);

    public Task<HoneypotRequestPage?> ListRequestsAsync(HoneypotSessionId id, int limit, HoneypotRequestId? after, CancellationToken cancellationToken = default) =>
        honeypot.ListRequestsAsync(id, limit, after, cancellationToken);
}
