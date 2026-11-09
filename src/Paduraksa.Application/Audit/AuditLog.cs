using System.Buffers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Paduraksa.Application.Storage;
using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Data.Audit;

namespace Paduraksa.Application.Audit;

/// <summary>
/// The append-only, hash-chained audit log. Each event's hash covers the previous event's hash
/// (<see cref="AuditChain"/>), so <see cref="AuditChainVerifier"/> can tell when rows were edited, deleted or
/// reordered. Thread-safe; the caller owns the context and the transaction.
/// </summary>
/// <remarks>
/// Internal: the config command pipeline (<see cref="Commands.ConfigService"/>) wraps it, and its API takes a
/// <see cref="ConfigDbContext"/>, which must not appear on Application's public surface.
/// </remarks>
internal sealed partial class AuditLog
{
    public const int MaxActionLength = 128;

    public const int MaxDetailsLength = 64 * 1024;

    private static readonly SearchValues<char> PrefixChars = SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789");
    private static readonly SearchValues<char> Base62Chars = SearchValues.Create(Base62.Alphabet);

    private readonly TimeProvider _timeProvider;
    private readonly Int64OrderedIdGenerator _ids;
    private readonly ILogger _logger;

    /// <param name="timeProvider">
    /// The one clock for both <see cref="AuditRecord.Timestamp"/> and the event IDs (from an Int64Ordered generator
    /// of this log's own), so the two can't disagree.
    /// </param>
    /// <param name="logger">Warns when the clock is behind the latest stored event.</param>
    public AuditLog(TimeProvider timeProvider, ILogger<AuditLog>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        _ids = new Int64OrderedIdGenerator(timeProvider);
        _logger = logger ?? NullLogger<AuditLog>.Instance;
    }

    /// <summary>
    /// Appends <paramref name="entry"/> after the latest event, chained to its hash. Must run inside the caller's
    /// transaction (with the change it records, if any); this saves the context (flushing anything else pending in
    /// it), the caller commits. If a concurrent writer appended in between, the save fails on the unique
    /// <c>PrevHash</c> index instead of forking the chain.
    /// </summary>
    /// <remarks>
    /// The chain runs in ID order, so a new ID must be above the latest event's. If the clock is behind it (e.g. a
    /// restart on a clock that went back), the generator observes the latest ID and issues the next one above it,
    /// with a warning; the event's timestamp still says what the clock said.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No transaction is active.</exception>
    /// <exception cref="ArgumentException">The entry is invalid (see <see cref="AuditEntry"/>).</exception>
    public async Task<AuditRecord> AppendAsync(ConfigDbContext context, AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        StoreChecks.RequireTransaction(context, "Appending an audit event");
        Validate(entry);
        string? details = DetailsOf(entry);

        var latest = await context.AuditEvents
            .OrderByDescending(e => e.Id)
            .Select(e => new { e.Id, e.Hash })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var id = AuditEventId.FromValue(_ids.Next());
        if (latest is not null && id <= latest.Id)
        {
            LogClockBehind(id, latest.Id);
            _ids.Observe(latest.Id.Value);
            id = AuditEventId.FromValue(_ids.Next());
        }

        var record = new AuditRecord(
            id,
            new DateTimeOffset(_timeProvider.GetUtcNow().UtcTicks, TimeSpan.Zero),
            entry.Actor,
            entry.Channel,
            entry.Action,
            entry.Target,
            entry.Outcome,
            entry.ClientIp is { } ip ? (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString() : null,
            details,
            entry.RevisionId,
            latest?.Hash ?? AuditChain.GenesisPrevHash,
            default);
        record = record with { Hash = AuditChain.ComputeHash(record) };

        context.AuditEvents.Add(AuditMapping.ToEntity(record));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    private static string? DetailsOf(AuditEntry entry)
    {
        if (!entry.Force && !entry.ViaAdminProxy)
        {
            return entry.Details;
        }

        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            if (entry.Details is { } details)
            {
                using JsonDocument existing = JsonDocument.Parse(details);
                if (existing.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty property in existing.RootElement.EnumerateObject())
                    {
                        property.WriteTo(writer);
                    }
                }
            }

            if (entry.Force)
            {
                writer.WriteBoolean("force", true);
            }

            if (entry.ViaAdminProxy)
            {
                writer.WriteBoolean("viaAdminProxy", true);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void Validate(AuditEntry entry)
    {
        StoreChecks.RequireDefined(entry.Actor, nameof(entry));
        StoreChecks.RequireDefined(entry.Channel, nameof(entry));
        StoreChecks.RequireDefined(entry.Outcome, nameof(entry));

        if (!IsActionName(entry.Action))
        {
            throw new ArgumentException(
                $"'{entry.Action}' is not an action name: lowercase [a-z0-9_] segments joined by dots, at least two, at most {MaxActionLength} chars.",
                nameof(entry));
        }

        if (entry.Target is { } target && !IsTypedIdString(target))
        {
            throw new ArgumentException($"'{target}' is not a typed-ID string (prefix_base62).", nameof(entry));
        }

        if (entry.Details is { } details)
        {
            if (details.Length > MaxDetailsLength)
            {
                throw new ArgumentException($"Details are limited to {MaxDetailsLength} chars.", nameof(entry));
            }

            var utf8 = StoreChecks.StrictUtf8(details, nameof(entry)); // lone surrogates wouldn't survive storage
            JsonValueKind kind;
            try
            {
                using var json = JsonDocument.Parse(utf8);
                kind = json.RootElement.ValueKind;
            }
            catch (JsonException e)
            {
                throw new ArgumentException($"Details are not well-formed JSON: {e.Message}", nameof(entry), e);
            }

            if (kind != JsonValueKind.Object)
            {
                throw new ArgumentException("Details must be a JSON object.", nameof(entry));
            }
        }
    }

    private static bool IsActionName(string? action)
    {
        if (string.IsNullOrEmpty(action) || action.Length > MaxActionLength)
        {
            return false;
        }

        var segments = action.Split('.');
        return segments.Length >= 2 && segments.All(s => s.Length > 0 && s.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'));
    }

    /// <summary>
    /// The shape of any typed ID's string form: a lowercase prefix, <c>_</c>, then 22 (UUID kinds) or 11 (Int64Ordered)
    /// base62 characters. The prefix isn't checked against the known ID types: a target can name any of them.
    /// </summary>
    private static bool IsTypedIdString(string value)
    {
        var separator = value.LastIndexOf('_');
        if (separator <= 0)
        {
            return false;
        }

        var prefix = value.AsSpan(0, separator);
        var digits = value.AsSpan(separator + 1);
        return prefix[0] is >= 'a' and <= 'z'
            && !prefix.ContainsAnyExcept(PrefixChars)
            && digits.Length is 22 or 11
            && !digits.ContainsAnyExcept(Base62Chars);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The audit ID generator issued {Issued}, not above the latest event {Latest}: the clock is behind the stored log. Continuing above it.")]
    private partial void LogClockBehind(AuditEventId issued, AuditEventId latest);
}

/// <summary>Entity ↔ record, the one place that knows both shapes.</summary>
internal static class AuditMapping
{
    public static AuditEvent ToEntity(AuditRecord r) => new()
    {
        Id = r.Id,
        Timestamp = r.Timestamp,
        ActorKind = r.Actor.Kind,
        ActorUserId = r.Actor.UserId,
        ActorTokenId = r.Actor.TokenId,
        Channel = r.Channel,
        Action = r.Action,
        Target = r.Target,
        Outcome = r.Outcome,
        ClientIp = r.ClientIp,
        Details = r.Details,
        RevisionId = r.RevisionId,
        PrevHash = r.PrevHash.ToArray(),
        Hash = r.Hash.ToArray(),
    };

    public static AuditRecord ToRecord(AuditEvent e) => new(
        e.Id,
        e.Timestamp,
        Actor.FromParts(e.ActorKind, e.ActorUserId, e.ActorTokenId),
        e.Channel,
        e.Action,
        e.Target,
        e.Outcome,
        e.ClientIp,
        e.Details,
        e.RevisionId,
        e.PrevHash,
        e.Hash);
}
