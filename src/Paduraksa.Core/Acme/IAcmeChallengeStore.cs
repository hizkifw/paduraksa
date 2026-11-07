namespace Paduraksa.Core.Acme;

/// <summary>
/// The HTTP-01 answers the public listener serves (PLAN §7.2 C, §9.3): a token the CA will fetch, and the key
/// authorization to answer it with. The orchestrator publishes before it tells the CA to validate and removes the entry
/// afterwards; the gate answers only tokens that are in the store, so nothing else under
/// <c>/.well-known/acme-challenge/</c> is ever served from here.
/// </summary>
/// <remarks>
/// The store is in memory: a restart forgets every entry, and an interrupted issuance publishes again when it resumes. A key
/// authorization is derived from the account key's thumbprint and a one-time token, so it is not secret key material,
/// but it is not logged either.
/// </remarks>
public interface IAcmeChallengeStore
{
    /// <summary>The most entries the store holds at once; <see cref="Publish"/> refuses more, so a runaway caller cannot grow it without bound.</summary>
    public const int MaxEntries = 1024;

    /// <summary>
    /// Makes <paramref name="keyAuthorization"/> the answer for <paramref name="token"/>. Publishing a token again
    /// replaces its answer and counts one more holder: the token is answered until every holder called <see cref="Remove"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The token is not a non-empty base64url string, or the key authorization is empty or not printable ASCII.</exception>
    /// <exception cref="InvalidOperationException">The store already holds <see cref="MaxEntries"/> other tokens.</exception>
    void Publish(string token, string keyAuthorization);

    /// <summary>Gives up one hold on <paramref name="token"/>; the last one stops the answering. Returns false if it was not published.</summary>
    bool Remove(string token);
}
