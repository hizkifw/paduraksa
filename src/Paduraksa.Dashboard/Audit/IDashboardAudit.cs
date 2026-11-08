using Paduraksa.Application.Audit;
using Paduraksa.Contracts.Audit;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Audit;

/// <summary>
/// What the audit page asks of the Application layer (PLAN §5.2): filtered pages of the log, newest first, and the hash-chain check. A
/// seam so the page can be tested without a database; <see cref="DashboardAudit"/> is the real one. Not authorized here, as on the API:
/// the page checks <c>audit:read</c> first.
/// </summary>
public interface IDashboardAudit
{
    /// <summary>Events matching <paramref name="filter"/>, newest first, after <paramref name="after"/> (the last event of the previous page).</summary>
    Task<AuditPage> QueryAsync(AuditFilter filter, int limit, AuditEventId? after, CancellationToken cancellationToken = default);

    /// <summary>Walks the whole chain and reports the first event that doesn't verify.</summary>
    Task<AuditVerification> VerifyAsync(CancellationToken cancellationToken = default);
}

/// <summary><see cref="AuditQueryService"/> behind <see cref="IDashboardAudit"/>.</summary>
internal sealed class DashboardAudit(AuditQueryService audit) : IDashboardAudit
{
    public Task<AuditPage> QueryAsync(AuditFilter filter, int limit, AuditEventId? after, CancellationToken cancellationToken = default) =>
        audit.QueryAsync(filter, limit, after, cancellationToken);

    public Task<AuditVerification> VerifyAsync(CancellationToken cancellationToken = default) => audit.VerifyAsync(cancellationToken);
}
