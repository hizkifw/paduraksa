using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Paduraksa.Application.Audit;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Application.Issuance;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Contracts.Notifications;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Data;
using Paduraksa.Data.Notifications;
using Paduraksa.Notifications;

namespace Paduraksa.Application.Notifications;

/// <summary>
/// Notification channels: add, replace, delete and test. An operational table like DNS providers (PLAN §5.1): a channel holds delivery state
/// outside the config document and secrets that must never be in one, so it is not a revision, and each change is audited on its own
/// (<c>notification.channel.*</c>). The webhook URL, token or password and a webhook's signing key go into the vault the moment they arrive; the
/// row keeps only their IDs, and nothing here returns, logs or audits a value.
/// </summary>
public sealed partial class NotificationService
{
    /// <summary>The longest label.</summary>
    public const int MaxLabelLength = NotificationValidator.MaxLabelLength;

    private readonly IDbContextFactory<ConfigDbContext> _contexts;
    private readonly ConfigService _config;
    private readonly ISecretStore _secrets;
    private readonly INotificationSenders _senders;
    private readonly NotificationOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly IPermissionEvaluator _permissions;
    private readonly NotificationStats _stats;

    internal NotificationService(
        IDbContextFactory<ConfigDbContext> contexts,
        ConfigService config,
        ISecretStore secrets,
        INotificationSenders senders,
        NotificationOptions options,
        TimeProvider time,
        ILogger<NotificationService> logger,
        IPermissionEvaluator permissions,
        NotificationStats stats)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(senders);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(stats);
        _contexts = contexts;
        _config = config;
        _secrets = secrets;
        _senders = senders;
        _options = options;
        _time = time;
        _logger = logger;
        _permissions = permissions;
        _stats = stats;
    }

    public async Task<NotificationChannelResult> CreateAsync(NotificationChannelBody body, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(origin);
        if (!await PermissionGate.MayAsync(_permissions, _config, origin, Permission.ProvidersWrite, "notification.channel.create", null, cancellationToken).ConfigureAwait(false))
        {
            return new NotificationChannelResult(IssuanceChangeStatus.Denied);
        }

        if (NotificationValidator.Check(body, isNew: true) is { Count: > 0 } errors)
        {
            return new NotificationChannelResult(IssuanceChangeStatus.Invalid, Errors: errors);
        }

        var label = body.Label.Trim();
        SecretId? secret = null;
        SecretId? signing = null;
        try
        {
            if (!string.IsNullOrEmpty(body.Secret))
            {
                secret = await PutSecretAsync(body.Secret, label, cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(body.SigningSecret))
            {
                signing = await PutSigningAsync(body.SigningSecret, label, cancellationToken).ConfigureAwait(false);
            }

            var now = _time.GetUtcNow();
            var row = new NotificationChannelRow { Id = NotificationChannelId.New(), CreatedAt = now };
            Apply(row, body, label, now);
            row.SecretId = secret;
            row.SigningSecretId = signing;
            var result = await _config.ApplyOperationalChangeAsync(
                async context =>
                {
                    context.NotificationChannels.Add(row);
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    var audit = IssuanceMapping.Entry(origin, "notification.channel.create", row.Id, AuditOutcome.Success) with
                    {
                        Details = AuditDetails.Json(w => w.WriteString("kind", row.Kind.ToName())),
                    };
                    return (new NotificationChannelResult(IssuanceChangeStatus.Committed, NotificationMapping.ToSummary(row)), audit);
                },
                reason => Unavailable(reason),
                cancellationToken).ConfigureAwait(false);
            if (result.Status != IssuanceChangeStatus.Committed)
            {
                await DeleteSecretsAsync([secret, signing], cancellationToken).ConfigureAwait(false);
            }
            else
            {
                LogCreated(row.Id, row.Kind);
            }

            return result;
        }
        catch
        {
            await DeleteSecretsAsync([secret, signing], cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Replaces a channel's settings. Its kind is fixed. A secret left out keeps the stored one; an empty one removes an optional secret (an ntfy token,
    /// an SMTP password without a login, a webhook's signing key).
    /// </summary>
    public async Task<NotificationChannelResult> UpdateAsync(NotificationChannelId id, NotificationChannelBody body, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(origin);
        if (!await PermissionGate.MayAsync(_permissions, _config, origin, Permission.ProvidersWrite, "notification.channel.update", id.ToString(), cancellationToken).ConfigureAwait(false))
        {
            return new NotificationChannelResult(IssuanceChangeStatus.Denied);
        }

        if (NotificationValidator.Check(body, isNew: false) is { Count: > 0 } errors)
        {
            return new NotificationChannelResult(IssuanceChangeStatus.Invalid, Errors: errors);
        }

        var label = body.Label.Trim();
        List<SecretId> orphaned = [];
        SecretId? created = null;
        SecretId? createdSigning = null;
        var dropped = 0;
        NotificationChannelKind? droppedKind = null;
        try
        {
            var result = await _config.ApplyOperationalChangeAsync(
                async context =>
                {
                    var row = await context.NotificationChannels.FirstOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);
                    if (row is null)
                    {
                        return (new NotificationChannelResult(IssuanceChangeStatus.NotFound), null);
                    }

                    if (row.Kind != body.Kind)
                    {
                        return (
                            new NotificationChannelResult(IssuanceChangeStatus.Invalid, Errors: [new ConfigError("/kind", NotificationErrorCodes.KindFixed, "a channel's kind cannot change; add a new channel")]),
                            IssuanceMapping.Entry(origin, "notification.channel.update", id, AuditOutcome.Invalid));
                    }

                    var missing = RequiredSecretMissing(row, body);
                    if (missing is not null)
                    {
                        return (new NotificationChannelResult(IssuanceChangeStatus.Invalid, Errors: [missing]), IssuanceMapping.Entry(origin, "notification.channel.update", id, AuditOutcome.Invalid));
                    }

                    var now = _time.GetUtcNow();
                    var wasEnabled = row.Enabled;
                    Apply(row, body, label, now);
                    if (wasEnabled && !row.Enabled)
                    {
                        dropped = await NotificationOutbox.DropPendingAsync(context, id, NotificationOutbox.ChannelPaused, now, cancellationToken).ConfigureAwait(false);
                        droppedKind = row.Kind;
                    }

                    created = await ChangeSecretAsync(row.SecretId, body.Secret, label, orphaned, signing: false, cancellationToken).ConfigureAwait(false);
                    row.SecretId = created is { } fresh ? fresh : body.Secret is { Length: 0 } ? null : row.SecretId;
                    createdSigning = await ChangeSecretAsync(row.SigningSecretId, body.SigningSecret, label, orphaned, signing: true, cancellationToken).ConfigureAwait(false);
                    row.SigningSecretId = createdSigning is { } freshSigning ? freshSigning : body.SigningSecret is { Length: 0 } ? null : row.SigningSecretId;
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return (new NotificationChannelResult(IssuanceChangeStatus.Committed, NotificationMapping.ToSummary(row)), IssuanceMapping.Entry(origin, "notification.channel.update", id, AuditOutcome.Success));
                },
                reason => Unavailable(reason),
                cancellationToken).ConfigureAwait(false);
            if (result.Status == IssuanceChangeStatus.Committed)
            {
                CountDropped(droppedKind, dropped);
                await DeleteSecretsAsync([.. orphaned.Select(s => (SecretId?)s)], cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await DeleteSecretsAsync([created, createdSigning], cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        catch
        {
            await DeleteSecretsAsync([created, createdSigning], cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<NotificationChannelResult> DeleteAsync(NotificationChannelId id, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!await PermissionGate.MayAsync(_permissions, _config, origin, Permission.ProvidersWrite, "notification.channel.delete", id.ToString(), cancellationToken).ConfigureAwait(false))
        {
            return new NotificationChannelResult(IssuanceChangeStatus.Denied);
        }

        SecretId?[] secrets = [];
        var dropped = 0;
        NotificationChannelKind? droppedKind = null;
        var result = await _config.ApplyOperationalChangeAsync(
            async context =>
            {
                var row = await context.NotificationChannels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);
                if (row is null)
                {
                    return (new NotificationChannelResult(IssuanceChangeStatus.NotFound), null);
                }

                secrets = [row.SecretId, row.SigningSecretId];
                dropped = await NotificationOutbox.DropPendingAsync(context, id, NotificationOutbox.ChannelDeleted, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                droppedKind = row.Kind;
                await context.NotificationChannels.Where(c => c.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                return (new NotificationChannelResult(IssuanceChangeStatus.Committed), IssuanceMapping.Entry(origin, "notification.channel.delete", id, AuditOutcome.Success));
            },
            reason => Unavailable(reason),
            cancellationToken).ConfigureAwait(false);
        if (result.Status == IssuanceChangeStatus.Committed)
        {
            CountDropped(droppedKind, dropped);
            await DeleteSecretsAsync(secrets, cancellationToken).ConfigureAwait(false);
            LogDeleted(id);
        }

        return result;
    }

    public async Task<IReadOnlyList<NotificationChannelSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var rows = await context.NotificationChannels.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            return [.. rows.OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id).Select(NotificationMapping.ToSummary)];
        }
    }

    public async Task<NotificationChannelSummary?> GetAsync(NotificationChannelId id, CancellationToken cancellationToken = default) =>
        await FindRowAsync(id, cancellationToken).ConfigureAwait(false) is { } row ? NotificationMapping.ToSummary(row) : null;

    /// <summary>
    /// "Send test": one message through the channel now, without retries, so a person sees at once whether it works. Needs <c>providers:write</c>
    /// (it reaches out with the channel's secret), is audited with its verdict only, and updates the channel's last sent / last error like any delivery.
    /// </summary>
    public async Task<NotificationTestResult> TestAsync(NotificationChannelId id, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!await PermissionGate.MayAsync(_permissions, _config, origin, Permission.ProvidersWrite, "notification.channel.test", id.ToString(), cancellationToken).ConfigureAwait(false))
        {
            return new NotificationTestResult(Found: false, Delivered: false) { Denied = true };
        }

        var row = await FindRowAsync(id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return new NotificationTestResult(Found: false, Delivered: false);
        }

        var test = NotificationText.Test(row.Label, _time.GetUtcNow());
        string? error;
        if (_senders.Find(row.Kind) is not { } sender)
        {
            error = "This build cannot send to that kind of channel.";
        }
        else
        {
            try
            {
                await sender.SendAsync(NotificationMapping.ToTarget(row), NotificationAddress.Message(test, _options), cancellationToken).ConfigureAwait(false);
                error = null;
            }
            catch (NotificationDeliveryException ex)
            {
                error = ex.Message;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogTestUnexpected(id, ex.GetType().Name);
                error = "The test message could not be sent.";
            }
        }

        await NotificationChannelState.RecordAsync(_contexts, _time, id, error, _logger, cancellationToken).ConfigureAwait(false);
        await _config.TryAppendAuditAsync(IssuanceMapping.Entry(origin, "notification.channel.test", id, error is null ? AuditOutcome.Success : AuditOutcome.Failed) with
        {
            Details = AuditDetails.Json(w => w.WriteBoolean("delivered", error is null)),
        }).ConfigureAwait(false);
        return new NotificationTestResult(Found: true, Delivered: error is null, error);
    }

    private async Task<NotificationChannelRow?> FindRowAsync(NotificationChannelId id, CancellationToken cancellationToken)
    {
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            return await context.NotificationChannels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void Apply(NotificationChannelRow row, NotificationChannelBody body, string label, DateTimeOffset now)
    {
        row.Kind = body.Kind;
        row.Label = label;
        row.Enabled = body.Enabled;
        row.Events = [.. body.Events.Distinct().Select(e => e.ToName())];
        row.ServerUrl = body.ServerUrl?.Trim().TrimEnd('/');
        row.Topic = body.Topic;
        row.SmtpHost = body.SmtpHost?.Trim();
        row.SmtpPort = body.SmtpPort;
        row.SmtpSecurity = body.Kind == NotificationChannelKind.Email ? body.SmtpSecurity ?? SmtpSecurity.StartTls : null;
        row.SmtpUsername = string.IsNullOrEmpty(body.SmtpUsername) ? null : body.SmtpUsername;
        row.MailFrom = body.MailFrom?.Trim();
        row.MailTo = [.. body.MailTo?.Select(a => a.Trim()) ?? []];
        row.UpdatedAt = now;
    }

    /// <summary>A replacement that would leave a required secret empty (Gotify, Discord, webhook with none stored, and a login without a password).</summary>
    private static ConfigError? RequiredSecretMissing(NotificationChannelRow row, NotificationChannelBody body)
    {
        var hasSecret = body.Secret is { Length: > 0 } || (body.Secret is null && row.SecretId is not null);
        var needsSecret = row.Kind is NotificationChannelKind.Gotify or NotificationChannelKind.Discord or NotificationChannelKind.Webhook
            || (row.Kind == NotificationChannelKind.Email && !string.IsNullOrEmpty(body.SmtpUsername));
        return needsSecret && !hasSecret ? new ConfigError("/secret", NotificationErrorCodes.SecretRequired, "this channel needs its secret") : null;
    }

    /// <summary>
    /// Applies a secret field of a replacement: a new value replaces the stored one in place (the ID, and so every reference, stays), or is
    /// stored fresh when there was none; an empty value removes it (the old one is queued to be deleted once the row is saved). Returns the ID of a secret
    /// created here, so a failed change can take it back out.
    /// </summary>
    private async Task<SecretId?> ChangeSecretAsync(SecretId? existing, string? value, string label, List<SecretId> orphaned, bool signing, CancellationToken cancellationToken)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length == 0)
        {
            if (existing is { } gone)
            {
                orphaned.Add(gone);
            }

            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            if (existing is { } id)
            {
                try
                {
                    _ = await _secrets.ReplaceAsync(id, bytes, new SecretOptions(Label(label), null, null), cancellationToken).ConfigureAwait(false);
                    return null;
                }
                catch (KeyNotFoundException)
                {
                    // The vault lost it (restored from a backup): store it again under a new ID.
                }
                catch (ArgumentException) when (signing)
                {
                    // A MAC key's algorithm is fixed, and its options carry none to change; the value is replaced with the options kept.
                    _ = await _secrets.ReplaceAsync(id, bytes, null, cancellationToken).ConfigureAwait(false);
                    return null;
                }
            }

            return signing
                ? await _secrets.PutAsync(SecretKind.HmacSigningKey, bytes, new SecretOptions(Label(label), MacAlgorithm.HmacSha256, null), cancellationToken).ConfigureAwait(false)
                : await _secrets.PutAsync(SecretKind.NotificationSecret, bytes, new SecretOptions(Label(label), null, null), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string Label(string label) => $"Notification channel: {label}";

    private async Task<SecretId> PutSecretAsync(string value, string label, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            return await _secrets.PutAsync(SecretKind.NotificationSecret, bytes, new SecretOptions(Label(label), null, null), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task<SecretId> PutSigningAsync(string value, string label, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            return await _secrets.PutAsync(SecretKind.HmacSigningKey, bytes, new SecretOptions(Label(label), MacAlgorithm.HmacSha256, null), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task DeleteSecretsAsync(IEnumerable<SecretId?> secrets, CancellationToken cancellationToken)
    {
        foreach (var secret in secrets)
        {
            if (secret is { } id)
            {
                await SecretCleanup.TryDeleteAsync(_secrets, id, _logger, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The deliveries a deleted or paused channel dropped, for the queue depth and the dropped counter (the rows are marked in the change's own transaction).</summary>
    private void CountDropped(NotificationChannelKind? kind, int count)
    {
        _stats.PendingAdded(-count);
        if (kind is { } known)
        {
            _stats.Ended(known, NotificationDeliveryState.Dropped, count);
        }
    }

    private static NotificationChannelResult Unavailable(string reason) =>
        new(IssuanceChangeStatus.Unavailable, Errors: [new ConfigError(string.Empty, ConfigService.StoreUnavailableCode, $"The config store is unavailable, so notification changes are refused: {reason}.")]);

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification channel {Channel} ({Kind}) added.")]
    private partial void LogCreated(NotificationChannelId channel, NotificationChannelKind kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification channel {Channel} deleted.")]
    private partial void LogDeleted(NotificationChannelId channel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification channel {Channel}: a sender failed unexpectedly during a test ({Type}).")]
    private partial void LogTestUnexpected(NotificationChannelId channel, string type);
}

/// <summary>Turning an event into the message a sender takes: the same for the dispatcher and for the test button.</summary>
internal static class NotificationAddress
{
    public static NotificationMessage Message(NotificationEvent notification, NotificationOptions options) => new(
        notification.Kind,
        notification.Title,
        notification.Body,
        notification.Severity,
        notification.At,
        notification.LinkPath is { } path && options.AdminUrl is { } admin ? new Uri(admin, path.TrimStart('/')).ToString() : null,
        notification.Data);
}

/// <summary>A channel's last sent / last error, written by the dispatcher and the test button.</summary>
internal static partial class NotificationChannelState
{
    public static async Task RecordAsync(IDbContextFactory<ConfigDbContext> contexts, TimeProvider time, NotificationChannelId id, string? error, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var now = time.GetUtcNow();
                var channels = context.NotificationChannels.Where(c => c.Id == id);
                _ = error is null
                    ? await channels.ExecuteUpdateAsync(s => s.SetProperty(c => c.LastSentAt, now).SetProperty(c => c.LastError, (string?)null).SetProperty(c => c.LastErrorAt, (DateTimeOffset?)null), cancellationToken).ConfigureAwait(false)
                    : await channels.ExecuteUpdateAsync(s => s.SetProperty(c => c.LastError, error).SetProperty(c => c.LastErrorAt, now), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is DbUpdateException or Microsoft.Data.Sqlite.SqliteException)
        {
            LogRecordFailed(logger, id, ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification channel {Channel}: its delivery state could not be saved ({Type}).")]
    private static partial void LogRecordFailed(ILogger logger, NotificationChannelId channel, string type);
}
