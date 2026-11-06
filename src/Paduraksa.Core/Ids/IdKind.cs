namespace Paduraksa.Core.Ids;

/// <summary>How a typed ID's underlying value is generated and stored.</summary>
public enum IdKind
{
    /// <summary>Time-ordered UUID (RFC 9562 v7). Stored as a 16-byte big-endian BLOB. The default.</summary>
    UuidV7 = 0,

    /// <summary>Random UUID (RFC 9562 v4), for publicly visible IDs that must not leak their creation time.</summary>
    UuidV4 = 1,

    /// <summary>
    /// Time-ordered 63-bit integer (44-bit ms timestamp + 19-bit counter) for high-volume append-only tables.
    /// <b>Predictable and enumerable</b>: for internal/admin-only IDs, never for IDs exposed to untrusted clients.
    /// See <see cref="Int64OrderedIdGenerator"/>.
    /// </summary>
    Int64Ordered = 2,
}
