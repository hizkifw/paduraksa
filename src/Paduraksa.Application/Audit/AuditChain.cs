using System.Buffers.Binary;
using System.Security.Cryptography;
using Paduraksa.Application.Storage;
using Paduraksa.Core.Model;

namespace Paduraksa.Application.Audit;

/// <summary>
/// The audit log's hash chain (PLAN §6.4): <c>hash = SHA-256(prevHash ‖ canonical bytes of the event)</c>, where
/// <c>prevHash</c> is the previous event's hash in ID order and 32 zero bytes for the first event.
/// </summary>
/// <remarks>
/// <para>
/// <b>Canonical encoding</b> (version-free, stable): a sequence of fields in ascending tag order,
/// each <c>tag (1 byte) ‖ length (uint32, big-endian) ‖ value (length bytes)</c>. A null optional field is left out
/// entirely; an empty string is present with length 0. Every stored column is covered: <c>PrevHash</c> by the
/// prefix, everything else by a field; only <c>Hash</c> itself is not.
/// </para>
/// <list type="table">
/// <listheader><term>Tag</term><description>Field: value</description></listheader>
/// <item><term>1</term><description>ID: int64, big-endian (8 bytes)</description></item>
/// <item><term>2</term><description>timestamp: int64, big-endian, UTC ticks (100 ns units since 0001-01-01T00:00:00Z)</description></item>
/// <item><term>3</term><description>actor kind: UTF-8 name (<see cref="ModelNames"/>)</description></item>
/// <item><term>4</term><description>actor user: UTF-8 typed-ID string (<c>usr_...</c>); user actors only</description></item>
/// <item><term>5</term><description>actor token: UTF-8 typed-ID string (<c>tok_...</c>); token actors only</description></item>
/// <item><term>6</term><description>channel: UTF-8 name</description></item>
/// <item><term>7</term><description>action: UTF-8</description></item>
/// <item><term>8</term><description>target: UTF-8 typed-ID string; optional</description></item>
/// <item><term>9</term><description>outcome: UTF-8 name</description></item>
/// <item><term>10</term><description>client IP: UTF-8, the stored text; optional</description></item>
/// <item><term>11</term><description>details: UTF-8, the stored JSON text; optional</description></item>
/// <item><term>12</term><description>revision: UTF-8 typed-ID string (<c>rev_...</c>); optional</description></item>
/// </list>
/// <para>
/// Unambiguous because every value is length-prefixed and tagged. Stable because values are the stored text (or a
/// fixed-width integer), never a re-formatting that a library update could change, and because the format only
/// grows: a new column gets the next unused tag and must be optional (left out when absent), so every existing event
/// keeps encoding to the same bytes and its hash still verifies. Tags are never reused or reordered, and
/// <see cref="ModelNames"/> names never change.
/// </para>
/// </remarks>
internal static class AuditChain
{
    public const int HashLength = SHA256.HashSizeInBytes;

    private const byte IdTag = 1;
    private const byte TimestampTag = 2;
    private const byte ActorKindTag = 3;
    private const byte ActorUserTag = 4;
    private const byte ActorTokenTag = 5;
    private const byte ChannelTag = 6;
    private const byte ActionTag = 7;
    private const byte TargetTag = 8;
    private const byte OutcomeTag = 9;
    private const byte ClientIpTag = 10;
    private const byte DetailsTag = 11;
    private const byte RevisionTag = 12;

    /// <summary>The first event's <c>prevHash</c>: 32 zero bytes.</summary>
    public static ReadOnlyMemory<byte> GenesisPrevHash { get; } = new byte[HashLength];

    /// <summary>The canonical bytes of <paramref name="record"/> (every field but the two hashes; see remarks on <see cref="AuditChain"/>).</summary>
    /// <exception cref="ArgumentException">A string isn't valid UTF-16 (lone surrogate), or an enum value is undefined.</exception>
    public static byte[] Encode(AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        using var buffer = new MemoryStream();
        WriteInt64(buffer, IdTag, record.Id.Value);
        WriteInt64(buffer, TimestampTag, record.Timestamp.UtcTicks);
        WriteString(buffer, ActorKindTag, record.Actor.Kind.ToName());
        WriteString(buffer, ActorUserTag, record.Actor.UserId?.ToString());
        WriteString(buffer, ActorTokenTag, record.Actor.TokenId?.ToString());
        WriteString(buffer, ChannelTag, record.Channel.ToName());
        WriteString(buffer, ActionTag, record.Action);
        WriteString(buffer, TargetTag, record.Target);
        WriteString(buffer, OutcomeTag, record.Outcome.ToName());
        WriteString(buffer, ClientIpTag, record.ClientIp);
        WriteString(buffer, DetailsTag, record.Details);
        WriteString(buffer, RevisionTag, record.RevisionId?.ToString());
        return buffer.ToArray();
    }

    /// <summary><c>SHA-256(record.PrevHash ‖ Encode(record))</c>. <see cref="AuditRecord.Hash"/> is ignored.</summary>
    /// <exception cref="ArgumentException"><see cref="AuditRecord.PrevHash"/> isn't 32 bytes, or <see cref="Encode"/> fails.</exception>
    public static byte[] ComputeHash(AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.PrevHash.Length != HashLength)
        {
            throw new ArgumentException($"PrevHash must be {HashLength} bytes, not {record.PrevHash.Length}.", nameof(record));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(record.PrevHash.Span);
        hash.AppendData(Encode(record));
        return hash.GetHashAndReset();
    }

    private static void WriteInt64(MemoryStream buffer, byte tag, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        WriteField(buffer, tag, bytes);
    }

    private static void WriteString(MemoryStream buffer, byte tag, string? value)
    {
        if (value is not null)
        {
            WriteField(buffer, tag, StoreChecks.StrictUtf8(value, "record")); // strict: what is hashed must be what SQLite stores
        }
    }

    private static void WriteField(MemoryStream buffer, byte tag, ReadOnlySpan<byte> value)
    {
        Span<byte> header = stackalloc byte[1 + sizeof(uint)];
        header[0] = tag;
        BinaryPrimitives.WriteUInt32BigEndian(header[1..], (uint)value.Length);
        buffer.Write(header);
        buffer.Write(value);
    }
}
