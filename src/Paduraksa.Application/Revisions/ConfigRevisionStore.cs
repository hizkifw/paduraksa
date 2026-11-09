using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Paduraksa.Application.Storage;
using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Data.Revisions;

namespace Paduraksa.Application.Revisions;

/// <summary>
/// The config revision history: an append-only, linear list of whole config documents. Each
/// revision stores the canonical document bytes Brotli-compressed with the SHA-256 of the uncompressed bytes, and
/// every read checks that hash. Appends need the clock (an instance); reads need nothing but a context (static).
/// Thread-safe; the caller owns the context and, for appends, the transaction.
/// </summary>
/// <remarks>
/// Internal: the config command pipeline (<see cref="Commands.ConfigService"/>) wraps it, and its API takes a
/// <see cref="ConfigDbContext"/>, which must not appear on Application's public surface.
/// </remarks>
internal sealed class ConfigRevisionStore
{
    /// <summary>The largest document accepted, uncompressed. Config documents are kilobytes; this bounds a bad read.</summary>
    public const int MaxDocumentLength = 16 * 1024 * 1024;

    /// <summary>The most revisions <see cref="ListAsync"/> returns at once.</summary>
    public const int MaxPageSize = 500;

    /// <summary>The longest <see cref="NewRevision.Message"/> accepted, in chars.</summary>
    public const int MaxMessageLength = 1000;

    /// <summary>The longest <see cref="NewRevision.Summary"/> accepted, in chars.</summary>
    public const int MaxSummaryLength = 1024 * 1024;

    // Compression runs while the caller holds the write transaction. Quality 9 keeps nearly all of quality 11's
    // ratio on repetitive JSON; 10 and 11 switch to Brotli's much slower optimal parser (several times the CPU for a
    // few percent), which would lengthen every config write's lock for no practical gain.
    private const int BrotliQuality = 9;
    private const int BrotliWindow = 22;

    private readonly TimeProvider _timeProvider;
    private readonly UuidV7Generator _ids;

    /// <param name="timeProvider">The one clock for both <see cref="RevisionInfo.CreatedAt"/> and the revision IDs.</param>
    public ConfigRevisionStore(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        _ids = new UuidV7Generator(timeProvider);
    }

    /// <summary>
    /// A new revision ID from this store's clock, for a caller that needs the ID before it appends (the command
    /// pipeline labels the compiled snapshot with it); pass it as <see cref="NewRevision.Id"/>.
    /// </summary>
    public ConfigRevisionId NextId() => ConfigRevisionId.FromValue(_ids.Next());

    /// <summary>
    /// Commits <paramref name="revision"/> on top of the latest one: <c>Seq</c> = latest + 1 (1 for the first),
    /// parent = latest. Must run inside the caller's transaction, which also holds the projection and audit writes;
    /// this saves the context (flushing anything else pending in it), the caller commits. If a concurrent writer got
    /// there first the save fails on the unique <c>Seq</c>/parent indexes instead of forking history.
    /// </summary>
    /// <exception cref="InvalidOperationException">No transaction is active on <paramref name="context"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The ID is empty; the document is empty, too large or not JSON; the summary is not JSON or too long; the message is too long or
    /// has a lone surrogate; the actor or channel is undefined.
    /// </exception>
    public async Task<RevisionInfo> AppendAsync(ConfigDbContext context, NewRevision revision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        StoreChecks.RequireTransaction(context, "Appending a revision");
        Validate(revision);
        var document = Compress(revision.Document.Span); // before the read: the less work after it, the shorter the lock
        var documentHash = SHA256.HashData(revision.Document.Span);

        var latest = await context.ConfigRevisions
            .OrderByDescending(r => r.Seq)
            .Select(r => new { r.Id, r.Seq })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var entity = new ConfigRevision
        {
            Id = revision.Id ?? NextId(),
            Seq = (latest?.Seq ?? 0) + 1,
            ParentId = latest?.Id,
            ActorKind = revision.Actor.Kind,
            ActorUserId = revision.Actor.UserId,
            ActorTokenId = revision.Actor.TokenId,
            Channel = revision.Channel,
            Message = revision.Message,
            CreatedAt = new DateTimeOffset(_timeProvider.GetUtcNow().UtcTicks, TimeSpan.Zero),
            Document = document,
            DocumentHash = documentHash,
            Summary = revision.Summary,
        };
        context.ConfigRevisions.Add(entity);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToInfo(entity);
    }

    /// <summary>The latest revision with its document, or null before the first commit.</summary>
    public static async Task<StoredRevision?> GetLatestAsync(ConfigDbContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var entity = await context.ConfigRevisions.AsNoTracking()
            .OrderByDescending(r => r.Seq)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return entity is null ? null : ToStored(entity);
    }

    /// <summary>A revision with its document, or null if there is none with that ID.</summary>
    public static async Task<StoredRevision?> GetAsync(ConfigDbContext context, ConfigRevisionId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var entity = await context.ConfigRevisions.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            .ConfigureAwait(false);
        return entity is null ? null : ToStored(entity);
    }

    /// <summary>
    /// Revision metadata, newest first, without loading any document. Pages by <c>Seq</c>: pass the last
    /// <see cref="RevisionInfo.Seq"/> of a page as <paramref name="beforeSeq"/> to get the next (older) one.
    /// </summary>
    /// <param name="context">The context to read from.</param>
    /// <param name="limit">Page size, 1 to <see cref="MaxPageSize"/>.</param>
    /// <param name="beforeSeq">Only revisions with a smaller <c>Seq</c>; null starts at the latest.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<IReadOnlyList<RevisionInfo>> ListAsync(
        ConfigDbContext context, int limit, long? beforeSeq = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxPageSize);

        var query = context.ConfigRevisions.AsNoTracking();
        if (beforeSeq is { } before)
        {
            query = query.Where(r => r.Seq < before);
        }

        // Every column but Document: a page of metadata never reads the blobs.
        var rows = await query
            .OrderByDescending(r => r.Seq)
            .Take(limit)
            .Select(r => new ConfigRevision
            {
                Id = r.Id,
                Seq = r.Seq,
                ParentId = r.ParentId,
                ActorKind = r.ActorKind,
                ActorUserId = r.ActorUserId,
                ActorTokenId = r.ActorTokenId,
                Channel = r.Channel,
                Message = r.Message,
                CreatedAt = r.CreatedAt,
                DocumentHash = r.DocumentHash,
                Summary = r.Summary,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(ToInfo)];
    }

    private static void Validate(NewRevision revision)
    {
        if (revision.Id is { IsEmpty: true })
        {
            throw new ArgumentException("The revision ID can't be empty.", nameof(revision));
        }

        if (revision.Document.IsEmpty || revision.Document.Length > MaxDocumentLength)
        {
            throw new ArgumentException($"The document must be 1 to {MaxDocumentLength} bytes, not {revision.Document.Length}.", nameof(revision));
        }

        StoreChecks.RequireJson(revision.Document, nameof(revision));
        if (revision.Summary is null || revision.Summary.Length > MaxSummaryLength)
        {
            throw new ArgumentException($"The summary must be at most {MaxSummaryLength} chars of JSON.", nameof(revision));
        }

        StoreChecks.RequireJson(revision.Summary, nameof(revision));
        if (revision.Message is { } message)
        {
            if (message.Length > MaxMessageLength)
            {
                throw new ArgumentException($"The message is limited to {MaxMessageLength} chars.", nameof(revision));
            }

            StoreChecks.StrictUtf8(message, nameof(revision));
        }

        StoreChecks.RequireDefined(revision.Actor, nameof(revision));
        StoreChecks.RequireDefined(revision.Channel, nameof(revision));
    }

    /// <exception cref="RevisionIntegrityException">The stored metadata doesn't describe a revision.</exception>
    private static RevisionInfo ToInfo(ConfigRevision r)
    {
        Actor actor;
        try
        {
            actor = Actor.FromParts(r.ActorKind, r.ActorUserId, r.ActorTokenId);
        }
        catch (ArgumentException e)
        {
            throw new RevisionIntegrityException(r.Id, r.Seq, "its actor columns don't describe an actor", e);
        }

        if (r.DocumentHash.Length != SHA256.HashSizeInBytes)
        {
            throw new RevisionIntegrityException(r.Id, r.Seq, $"its document hash is {r.DocumentHash.Length} bytes, not {SHA256.HashSizeInBytes}");
        }

        return new RevisionInfo(r.Id, r.Seq, r.ParentId, actor, r.Channel, r.Message, r.CreatedAt, r.DocumentHash, r.Summary);
    }

    private static StoredRevision ToStored(ConfigRevision r)
    {
        byte[] document;
        try
        {
            document = Decompress(r.Document);
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException)
        {
            throw new RevisionIntegrityException(r.Id, r.Seq, "the document doesn't decompress", e);
        }

        if (!SHA256.HashData(document).AsSpan().SequenceEqual(r.DocumentHash))
        {
            throw new RevisionIntegrityException(r.Id, r.Seq, "the document doesn't match its SHA-256");
        }

        return new StoredRevision(ToInfo(r), document);
    }

    private static byte[] Compress(ReadOnlySpan<byte> document)
    {
        var buffer = new byte[BrotliEncoder.GetMaxCompressedLength(document.Length)];
        if (!BrotliEncoder.TryCompress(document, buffer, out var written, BrotliQuality, BrotliWindow))
        {
            throw new InvalidOperationException("Brotli compression failed."); // can't happen with a max-size buffer
        }

        return buffer[..written];
    }

    /// <exception cref="InvalidDataException">Not Brotli data, or it decompresses past <see cref="MaxDocumentLength"/>.</exception>
    private static byte[] Decompress(byte[] compressed)
    {
        using var brotli = new BrotliStream(new MemoryStream(compressed, writable: false), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = brotli.Read(buffer)) > 0)
        {
            if (output.Length + read > MaxDocumentLength)
            {
                throw new InvalidDataException($"The document decompresses past {MaxDocumentLength} bytes.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }
}
