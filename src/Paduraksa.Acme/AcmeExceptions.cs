namespace Paduraksa.Acme;

/// <summary>What a scheduler should do about a failed ACME call.</summary>
public enum AcmeFailureKind
{
    /// <summary>Repeating the same request will fail the same way: fix the cause, or give up and tell an operator.</summary>
    Permanent = 0,

    /// <summary>The CA or the network had trouble. Retry with backoff.</summary>
    Transient = 1,

    /// <summary>A CA limit applies. Retry no earlier than <see cref="AcmeException.RetryAfter"/> (if the CA sent one), and back off hard.</summary>
    RateLimited = 2,

    /// <summary>
    /// The CA answered something that is not valid ACME (a redirect, a malformed body, a missing nonce). Not the caller's
    /// fault and not obviously the CA's for good: retry with a long, capped backoff and tell an operator.
    /// </summary>
    ProtocolViolation = 3,
}

/// <summary>Base of every failure of the ACME client that is not a caller bug (those are <see cref="ArgumentException"/> and friends).</summary>
public abstract class AcmeException(string message, AcmeFailureKind kind, TimeSpan? retryAfter = null, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>Whether and how to retry; see <see cref="AcmeFailureKind"/>.</summary>
    public AcmeFailureKind Kind { get; } = kind;

    /// <summary>True for <see cref="AcmeFailureKind.Transient"/> and <see cref="AcmeFailureKind.RateLimited"/>: retry soon.</summary>
    public bool IsTransient => Kind is AcmeFailureKind.Transient or AcmeFailureKind.RateLimited;

    /// <summary>True for everything but <see cref="AcmeFailureKind.Permanent"/>: a retry is worth trying, at the backoff the kind calls for.</summary>
    public bool IsRetryable => Kind != AcmeFailureKind.Permanent;

    /// <summary>The CA's <c>Retry-After</c>, as a delay from when it was received; <see langword="null"/> if it sent none. Not capped.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// The CA answered a request with a problem document (RFC 8555 §6.7). The message carries the CA's detail with control
/// characters removed and cut to 512 characters; <see cref="Problem"/> keeps the raw text.
/// </summary>
public sealed class AcmeProblemException(AcmeProblem problem, Uri url, TimeSpan? retryAfter, Uri? termsOfServiceUrl = null)
    : AcmeException(
        $"ACME request to {AcmeText.Url(url)} failed: {AcmeText.Sanitize(problem.Type, 200)}"
            + (problem.Detail is null ? string.Empty : $" ({AcmeText.Sanitize(problem.Detail)})"),
        AcmeProblemTypes.Classify(problem.Type, problem.Status),
        retryAfter)
{
    public AcmeProblem Problem { get; } = problem;

    /// <summary>The problem type URN, e.g. <see cref="AcmeProblemTypes.RateLimited"/>.</summary>
    public string Type => Problem.Type;

    public string? Detail => Problem.Detail;

    public int? Status => Problem.Status;

    public IReadOnlyList<AcmeSubproblem> Subproblems => Problem.Subproblems;

    /// <summary>The URL of the request that failed.</summary>
    public Uri Url { get; } = url;

    /// <summary>
    /// The <c>Link: rel="terms-of-service"</c> of the response (an absolute http(s) URL, else <see langword="null"/>): with
    /// <see cref="AcmeProblemTypes.UserActionRequired"/> it is the page where the operator agrees to new terms. See also
    /// <see cref="AcmeProblem.Instance"/>.
    /// </summary>
    public Uri? TermsOfServiceUrl { get; } = termsOfServiceUrl;
}

/// <summary>
/// The CA's answer was not valid ACME: a redirect or another origin, a malformed or oversized body, a missing nonce, a
/// directory without the mandatory URLs. <see cref="AcmeFailureKind.ProtocolViolation"/>.
/// </summary>
public sealed class AcmeProtocolException(string message, Exception? inner = null)
    : AcmeException(message, AcmeFailureKind.ProtocolViolation, inner: inner);

/// <summary>The request never got an answer (connection failure, TLS failure, timeout) or a poll ran out of time. Transient.</summary>
public sealed class AcmeTransportException(string message, Exception? inner = null)
    : AcmeException(message, AcmeFailureKind.Transient, inner: inner);
