namespace Paduraksa.Acme;

/// <summary>An RFC 7807 problem document as ACME uses it (RFC 8555 §6.7), in a failed response or embedded in an order or challenge.</summary>
/// <param name="Type">The problem type URI, normally an <see cref="AcmeProblemTypes"/> URN; <c>about:blank</c> if the CA sent none.</param>
/// <param name="Detail">The human-readable explanation. CA-supplied text: show it, do not parse it.</param>
/// <param name="Status">The HTTP status the document carries, if it has one.</param>
/// <param name="Subproblems">The per-identifier problems of a <c>compound</c> error (RFC 8555 §6.7.1), empty if there are none.</param>
/// <param name="Instance">The problem's <c>instance</c> URI, if the CA sent one (<c>userActionRequired</c> uses it for the page to visit). CA-supplied: not necessarily a URL.</param>
public sealed record AcmeProblem(string Type, string? Detail, int? Status, IReadOnlyList<AcmeSubproblem> Subproblems, string? Instance = null);

/// <summary>One problem within a compound error, optionally tied to the identifier it concerns.</summary>
public sealed record AcmeSubproblem(string Type, string? Detail, AcmeIdentifier? Identifier);

/// <summary>The problem type URNs of RFC 8555 §6.7 and RFC 9773 that the client acts on.</summary>
public static class AcmeProblemTypes
{
    public const string Prefix = "urn:ietf:params:acme:error:";
    public const string BadNonce = Prefix + "badNonce";
    public const string RateLimited = Prefix + "rateLimited";
    public const string ServerInternal = Prefix + "serverInternal";
    public const string AccountDoesNotExist = Prefix + "accountDoesNotExist";
    public const string ExternalAccountRequired = Prefix + "externalAccountRequired";
    public const string OrderNotReady = Prefix + "orderNotReady";
    public const string AlreadyReplaced = Prefix + "alreadyReplaced";
    public const string AlreadyRevoked = Prefix + "alreadyRevoked";
    public const string Compound = Prefix + "compound";
    public const string UserActionRequired = Prefix + "userActionRequired";
    public const string InvalidProfile = Prefix + "invalidProfile";

    /// <summary>
    /// Whether a request that failed with this problem is worth repeating unchanged later: server trouble, an exhausted nonce
    /// retry, rate limiting. Everything else (a rejected identifier, a bad CSR, an unauthorized account) fails the same way
    /// every time and needs a change before it can succeed.
    /// </summary>
    public static AcmeFailureKind Classify(string type, int? status) => type switch
    {
        RateLimited => AcmeFailureKind.RateLimited,
        BadNonce or ServerInternal => AcmeFailureKind.Transient,
        _ when status == 429 => AcmeFailureKind.RateLimited,
        _ when status is >= 500 or 408 => AcmeFailureKind.Transient,
        _ => AcmeFailureKind.Permanent,
    };
}
