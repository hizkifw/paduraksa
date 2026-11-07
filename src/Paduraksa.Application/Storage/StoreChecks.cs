using System.Text;
using System.Text.Json;
using Paduraksa.Core.Model;
using Paduraksa.Data;

namespace Paduraksa.Application.Storage;

/// <summary>Argument and state checks shared by the revision store and the audit log.</summary>
internal static class StoreChecks
{
    // Strict: SQLite text is UTF-8, so a lone surrogate would be stored as U+FFFD instead of failing.
    private static readonly UTF8Encoding StrictEncoding = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Appends compute "the next one" (Seq, parent, previous hash) from the latest row, so they must run inside the
    /// caller's transaction: that is what makes the read and the insert one atomic step.
    /// </summary>
    /// <remarks>
    /// The concurrency safety relies on how the transaction starts: EF Core's <c>BeginTransaction</c> on
    /// Microsoft.Data.Sqlite issues <c>BEGIN IMMEDIATE</c>, which takes the database's write lock up front, so a second
    /// writer waits (up to the busy timeout) until the first commits and then reads its row as the latest. The unique
    /// indexes (Seq, ParentId, PrevHash) are the backstop if a transaction ever starts deferred and two writers read
    /// the same latest row: the second insert then fails instead of forking.
    /// </remarks>
    public static void RequireTransaction(ConfigDbContext context, string operation)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                $"{operation} must run inside the caller's transaction (context.Database.BeginTransactionAsync()).");
        }
    }

    public static void RequireDefined(Actor actor, string paramName)
    {
        if (!Enum.IsDefined(actor.Kind))
        {
            throw new ArgumentException("The actor is undefined (default(Actor)).", paramName);
        }
    }

    public static void RequireDefined<T>(T value, string paramName)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(paramName, value, $"Undefined {typeof(T).Name}.");
        }
    }

    /// <summary>Throws unless <paramref name="json"/> is one well-formed JSON value.</summary>
    public static void RequireJson(string json, string paramName)
    {
        ArgumentNullException.ThrowIfNull(json, paramName);
        RequireJson(StrictUtf8(json, paramName), paramName);
    }

    /// <inheritdoc cref="RequireJson(string, string)"/>
    public static void RequireJson(ReadOnlyMemory<byte> utf8Json, string paramName)
    {
        try
        {
            using var _ = JsonDocument.Parse(utf8Json);
        }
        catch (JsonException e)
        {
            throw new ArgumentException($"Not well-formed JSON: {e.Message}", paramName, e);
        }
    }

    /// <summary>The UTF-8 bytes of <paramref name="text"/>, refusing lone surrogates (which wouldn't survive storage).</summary>
    /// <exception cref="ArgumentException"><paramref name="text"/> isn't valid UTF-16.</exception>
    public static byte[] StrictUtf8(string text, string paramName)
    {
        try
        {
            return StrictEncoding.GetBytes(text);
        }
        catch (EncoderFallbackException e)
        {
            throw new ArgumentException("Text must be valid UTF-16 (no lone surrogates) to be stored faithfully.", paramName, e);
        }
    }
}
