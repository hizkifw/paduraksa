using Paduraksa.Application.Audit;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Data;

namespace Paduraksa.Application.Commands;

public sealed partial class ConfigService
{
    /// <summary>
    /// Runs a certificate change (<see cref="Certificates.CertificateService"/>) on the config writer's lock, so it is
    /// ordered with config commands and with their publishes. In one transaction: <paramref name="change"/>
    /// edits the inventory tables and says what to audit; the audit event is appended. When
    /// <see cref="CertificateChangeOutcome.Republish"/> is set, the <b>live config is compiled again with the new
    /// inventory before the commit</b> (the transaction sees its own rows, as a command's compile sees its candidate),
    /// then everything commits and the result is published, without a new revision, exactly as a command's snapshot is;
    /// this waits for it like a command does.
    /// </summary>
    /// <remarks>
    /// Once the transaction commits the change has happened: nothing after it can fail the call or take the caller's
    /// cancellation. A change that is refused, or that doesn't affect serving, publishes nothing. If the live config can't
    /// be compiled (a degraded start whose database config is broken) the change stands, nothing is published and the
    /// result has no <see cref="CertificateChangeResult.Publish"/>; the next commit, start or validity change compiles
    /// with the inventory anyway, because every compile reads it fresh. A degraded start on the last-known-good file
    /// republishes that document, never the empty config, and doesn't end the degraded state.
    /// </remarks>
    internal async Task<CertificateChangeResult> ApplyCertificateChangeAsync(
        CommandOrigin origin,
        Func<CertificateChangeScope, Task<CertificateChangeOutcome>> change,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(change);
        ObjectDisposedException.ThrowIf(_disposed, this);

        CertificateChangeResult result;
        PublishTicket? ticket = null;
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Status is { AcceptsCommands: false } unusable)
            {
                LogRefused("certificate change", unusable.Problem ?? "unknown");
                return new CertificateChangeResult(
                    CertificateChangeStatus.Unavailable,
                    Errors: [new ConfigError(string.Empty, StoreUnavailableCode, $"The config store is unavailable, so certificate changes are refused: {unusable.Problem}.")]);
            }

            var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                CertificateChangeOutcome outcome;
                LiveCompile? live = null;
                var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    // The transaction holds the write lock, so the latest revision can't move while the change looks at it.
                    var (_, document) = await LoadAsync(context, cancellationToken).ConfigureAwait(false);
                    outcome = await change(new CertificateChangeScope(context, document)).ConfigureAwait(false);
                    if (outcome.Audit is { } audit)
                    {
                        await _audit.AppendAsync(context, audit, cancellationToken).ConfigureAwait(false);
                    }

                    if (outcome.Republish)
                    {
                        live = await CompileLiveAsync(context, cancellationToken).ConfigureAwait(false);
                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }

                // Committed: from here on the change is done, whatever happens to the publish.
                result = outcome.Result;
                PublishOperational(outcome.Audit);
                if (live is not null)
                {
                    ticket = PublishLive(live);
                }
            }
        }
        finally
        {
            _writer.Release();
        }

        if (ticket is null)
        {
            return result;
        }

        var finished = await FinishPublishAsync(new Execution(new ConfigCommandResult(ConfigCommandStatus.Committed), ticket), origin).ConfigureAwait(false);
        return result with { Publish = finished.Publish };
    }
}

/// <summary>What a certificate change may look at: its transaction's context and the latest revision's document.</summary>
internal sealed record CertificateChangeScope(ConfigDbContext Context, ConfigDocument Document);

/// <summary>What a certificate change decided: its result, the audit event to record with it, and whether serving changed.</summary>
/// <param name="Result">The result the caller gets (without the publish outcome).</param>
/// <param name="Audit">The audit event, appended in the same transaction; null for a change that did nothing.</param>
/// <param name="Republish">The change affects what is served: recompile the live config and publish it.</param>
internal sealed record CertificateChangeOutcome(CertificateChangeResult Result, AuditEntry? Audit, bool Republish);
