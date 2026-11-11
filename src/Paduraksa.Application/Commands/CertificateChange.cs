using Paduraksa.Application.Audit;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Config;
using Paduraksa.Data;

namespace Paduraksa.Application.Commands;

/// <summary>What a certificate change may look at: its transaction's context and the latest revision's document.</summary>
internal sealed record CertificateChangeScope(ConfigDbContext Context, ConfigDocument Document);

/// <summary>What a certificate change decided: its result, the audit event to record with it, and whether serving changed.</summary>
/// <param name="Result">The result the caller gets (without the publish outcome).</param>
/// <param name="Audit">The audit event, appended in the same transaction; null for a change that did nothing.</param>
/// <param name="Republish">The change affects what is served: recompile the live config and publish it.</param>
internal sealed record CertificateChangeOutcome(CertificateChangeResult Result, AuditEntry? Audit, bool Republish);
