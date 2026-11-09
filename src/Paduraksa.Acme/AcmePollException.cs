namespace Paduraksa.Acme;

/// <summary>Why a <c>WaitFor...</c> call stopped polling.</summary>
public enum AcmePollFailure
{
    /// <summary>The resource was still not in a final state when <see cref="AcmeClientOptions.PollTimeout"/> ran out.</summary>
    Timeout = 0,

    /// <summary>
    /// The CA asked for a <c>Retry-After</c> longer than <see cref="AcmeClientOptions.MaxPollDelay"/>. The client does not
    /// poll earlier than the CA asked, nor block for that long: the caller retries at <see cref="AcmeException.RetryAfter"/>.
    /// </summary>
    RetryAfterTooLong = 1,
}

/// <summary>
/// A <c>WaitFor...</c> call gave up while the CA was answering fine: unlike <see cref="AcmeTransportException"/> ("the
/// network is down"), the resource is reachable and still in progress, and <see cref="Status"/> and the last copy of it say
/// where it stands. Transient.
/// </summary>
public sealed class AcmePollException(
    string message, AcmePollFailure reason, Uri url, AcmeStatus status, object resource, TimeSpan? retryAfter)
    : AcmeException(message, AcmeFailureKind.Transient, retryAfter)
{
    public AcmePollFailure Reason { get; } = reason;

    public Uri Url { get; } = url;

    public AcmeStatus Status { get; } = status;

    /// <summary>The order at the last poll, when an order was being polled.</summary>
    public AcmeOrder? Order { get; } = resource as AcmeOrder;

    /// <summary>The authorization at the last poll, when an authorization was being polled.</summary>
    public AcmeAuthorization? Authorization { get; } = resource as AcmeAuthorization;
}
