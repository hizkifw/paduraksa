namespace Paduraksa.TypedIds.EntityFrameworkCore;

/// <summary>Settings for <see cref="TypedIdModelConfigurationBuilderExtensions.AddTypedIdConventions"/>.</summary>
public sealed class TypedIdConventionOptions
{
    /// <summary>How Guid-backed IDs (<see cref="IdKind.UuidV7"/>, <see cref="IdKind.UuidV4"/>) are stored in columns.</summary>
    public TypedIdGuidColumns GuidColumns { get; set; } = TypedIdGuidColumns.Binary;
}

/// <summary>Column storage of Guid-backed typed IDs. <see cref="IdKind.Int64Ordered"/> IDs are always <see cref="long"/>.</summary>
public enum TypedIdGuidColumns
{
    /// <summary>
    /// A 16-byte big-endian binary column, which sorts in RFC 9562 order on every provider, so UUIDv7 IDs stay in
    /// creation order.
    /// </summary>
    Binary = 0,

    /// <summary>
    /// A <see cref="Guid"/> column, so the provider picks its own type (PostgreSQL <c>uuid</c>). Only right where the
    /// provider compares Guids in RFC 9562 byte order: SQL Server's <c>uniqueidentifier</c> does not, and SQLite and
    /// MySQL store a Guid as 36 characters of text.
    /// </summary>
    Native = 1,
}
