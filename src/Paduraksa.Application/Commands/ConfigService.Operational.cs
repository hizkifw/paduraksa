using Paduraksa.Application.Audit;
using Paduraksa.Contracts.Commands;
using Paduraksa.Data;

namespace Paduraksa.Application.Commands;

public sealed partial class ConfigService
{
    private OperationalChangeFeed? _operationalChanges;

    /// <summary>
    /// The operational change feed: one notification per committed, audited change made through
    /// <see cref="ApplyOperationalChangeAsync{T}"/> or <see cref="ApplyCertificateChangeAsync"/> (certificates, ACME accounts, DNS
    /// providers, ACME orders), so the dashboard's lists follow them without polling.
    /// </summary>
    public OperationalChangeFeed OperationalChanges =>
        LazyInitializer.EnsureInitialized(ref _operationalChanges, () => new OperationalChangeFeed(_logger));

    /// <summary>Tells the operational feed about a committed change that was audited.</summary>
    private void PublishOperational(AuditEntry? audit)
    {
        if (audit is not null)
        {
            OperationalChanges.Publish(new OperationalChange(audit.Action, audit.Target));
        }
    }

    /// <summary>
    /// Runs a change to an operational resource that is neither config nor serving metadata (ACME accounts, DNS
    /// providers, issuance state) on the config writer's lock: in one transaction <paramref name="change"/> edits the
    /// tables and says what to audit, the audit event is appended to the hash chain, and everything commits. Nothing is
    /// compiled or published and no revision is made. Going through the writer keeps these audit events ordered with
    /// every other one, so two writers can't fork the chain.
    /// </summary>
    /// <param name="change">Edits through the given context and returns the result with the audit entry (null for none).</param>
    /// <param name="unavailable">Makes the result for a degraded start (the config store is unusable); called with the reason, nothing runs.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer and the work before the commit.</param>
    internal async Task<T> ApplyOperationalChangeAsync<T>(
        Func<ConfigDbContext, Task<(T Result, AuditEntry? Audit)>> change,
        Func<string, T> unavailable,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(unavailable);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Status is { AcceptsCommands: false } unusable)
            {
                LogRefused("operational change", unusable.Problem ?? "unknown");
                return unavailable(unusable.Problem ?? "unknown");
            }

            var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    var (result, audit) = await change(context).ConfigureAwait(false);
                    if (audit is not null)
                    {
                        await _audit.AppendAsync(context, audit, cancellationToken).ConfigureAwait(false);
                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    PublishOperational(audit);
                    return result;
                }
            }
        }
        finally
        {
            _writer.Release();
        }
    }
}
