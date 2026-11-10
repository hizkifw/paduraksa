using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

using Paduraksa.Core.Acme;

namespace Paduraksa.Proxy.Gate;

/// <summary>The in-memory HTTP-01 answers. The orchestrator publishes through <see cref="IAcmeChallengeStore"/>; the
/// gate reads through <see cref="TryGet"/>, which takes the token as a slice of the request path and compares it
/// exactly, so a lookup allocates nothing and a hit answers with the response built when the entry was published.
/// Thread-safe.</summary>
public sealed class AcmeChallengeStore : IAcmeChallengeStore
{
    /// <summary>
    /// The path an HTTP-01 response is served under (RFC 8555 §8.3). Must match <c>AcmeChallenges.Http01PathPrefix</c> in Paduraksa.Acme,
    /// which Proxy cannot reference.
    /// </summary>
    public const string PathPrefix = "/.well-known/acme-challenge/";

    private const int MaxTokenLength = 512;
    private const int MaxKeyAuthorizationLength = 1024;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Entry>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    private readonly Lock _writers = new();

    private readonly int _capacity;

    public AcmeChallengeStore()
        : this(IAcmeChallengeStore.MaxEntries)
    {
    }

    /// <summary>A store that holds at most <paramref name="capacity"/> tokens (tests use a small one to reach the limit).</summary>
    internal AcmeChallengeStore(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _lookup = _entries.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>The number of published tokens.</summary>
    public int Count => _entries.Count;

    /// <inheritdoc />
    /// <remarks>Counted: a token published by two issuances (they share a pending authorization) is answered until both removed it.</remarks>
    public void Publish(string token, string keyAuthorization)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(keyAuthorization);
        if (!IsBase64Url(token))
        {
            throw new ArgumentException("A challenge token must be a non-empty base64url string.", nameof(token));
        }

        if (keyAuthorization.Length is 0 or > MaxKeyAuthorizationLength || !IsPrintableAscii(keyAuthorization))
        {
            throw new ArgumentException("A key authorization must be 1 to 1024 printable ASCII characters.", nameof(keyAuthorization));
        }

        var response = PrecomputedResponse.Create(200, System.Text.Encoding.ASCII.GetBytes(keyAuthorization), "text/plain");

        // The cap check and the add must not interleave, or two publishers could both pass at MaxEntries - 1.
        lock (_writers)
        {
            if (!_entries.ContainsKey(token) && _entries.Count >= _capacity)
            {
                throw new InvalidOperationException($"The challenge store already holds {_capacity} tokens.");
            }

            _entries[token] = new Entry(response, _entries.TryGetValue(token, out var existing) ? existing.Holders + 1 : 1);
        }
    }

    /// <inheritdoc />
    public bool Remove(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        lock (_writers)
        {
            if (!_entries.TryGetValue(token, out var entry))
            {
                return false;
            }

            if (entry.Holders > 1)
            {
                _entries[token] = new Entry(entry.Response, entry.Holders - 1);
            }
            else
            {
                _entries.TryRemove(token, out _);
            }

            return true;
        }
    }

    /// <summary>The response for exactly <paramref name="token"/>, or false when it is not published (or is empty or oversized). 0 B.</summary>
    internal bool TryGet(ReadOnlySpan<char> token, [NotNullWhen(true)] out PrecomputedResponse? response)
    {
        if (token.IsEmpty || token.Length > MaxTokenLength)
        {
            response = null;
            return false;
        }

        if (_lookup.TryGetValue(token, out var entry))
        {
            response = entry.Response;
            return true;
        }

        response = null;
        return false;
    }

    private sealed record Entry(PrecomputedResponse Response, int Holders);

    private static bool IsBase64Url(string value)
    {
        if (value.Length is 0 or > MaxTokenLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPrintableAscii(string value)
    {
        foreach (var c in value)
        {
            if (c is < '!' or > '~')
            {
                return false;
            }
        }

        return true;
    }
}
