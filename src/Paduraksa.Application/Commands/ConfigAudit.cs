using System.Data.Common;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Paduraksa.Application.Audit;
using Paduraksa.Core.Model;
using Paduraksa.Data;

namespace Paduraksa.Application.Commands;

/// <summary>How the config writer records audit events: in the transaction that makes the change, or on their own once an outcome is decided.</summary>
internal sealed partial class ConfigAudit
{
    /// <summary>The most error codes and publish errors an audit event carries.</summary>
    public const int MaxAuditedCodes = 20;

    public const int MaxAuditedErrorLength = 500;

    private readonly IDbContextFactory<ConfigDbContext> _contexts;
    private readonly AuditLog _log;
    private readonly ILogger _logger;

    public ConfigAudit(IDbContextFactory<ConfigDbContext> contexts, TimeProvider timeProvider, ILoggerFactory loggerFactory)
    {
        _contexts = contexts;
        _log = new AuditLog(timeProvider, loggerFactory.CreateLogger<AuditLog>());
        _logger = loggerFactory.CreateLogger<ConfigAudit>();
    }

    /// <summary>Appends inside the caller's open transaction.</summary>
    public Task AppendAsync(ConfigDbContext context, AuditEntry entry, CancellationToken cancellationToken) =>
        _log.AppendAsync(context, entry, cancellationToken);

    /// <summary>
    /// Appends an audit event in a transaction of its own, once the outcome it records is decided: without the
    /// caller's cancellation, and logging rather than throwing when the database refuses, so the outcome still reaches
    /// the caller. A fresh context from the factory, so it also works for a publish that completes later.
    /// </summary>
    public async Task TryAppendAsync(AuditEntry entry)
    {
        try
        {
            var context = await _contexts.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var transaction = await context.Database.BeginTransactionAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    await _log.AppendAsync(context, entry, CancellationToken.None).ConfigureAwait(false);
                    await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException or InvalidOperationException)
        {
            // InvalidOperationException includes ObjectDisposedException: the container is shutting down.
            LogAuditFailed(ex, entry.Action, entry.Outcome, entry.Target);
        }
    }

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Couldn't write the audit event {Action} ({Outcome}, target {Target}); the outcome stands.")]
    private partial void LogAuditFailed(Exception exception, string action, AuditOutcome outcome, string? target);
}
