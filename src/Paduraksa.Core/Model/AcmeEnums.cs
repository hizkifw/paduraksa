namespace Paduraksa.Core.Model;

/// <summary>The state of an ACME account row. Persisted by name (<see cref="ModelNames"/>).</summary>
public enum AcmeAccountStatus
{
    /// <summary>The CA knows the account and it may order certificates.</summary>
    Active = 1,

    /// <summary>Deactivated at the CA (RFC 8555 §7.3.6): final, nothing can be ordered with it any more.</summary>
    Deactivated = 2,
}

/// <summary>
/// Where an ACME order stands in Paduraksa's own bookkeeping. The CA's order is the authority on what
/// happens next, so a resume re-reads it; this status is for operators and for the scheduler to see what is unfinished.
/// Persisted by name (<see cref="ModelNames"/>).
/// </summary>
public enum AcmeOrderStatus
{
    /// <summary>Requested; the CA has not been asked for an order yet.</summary>
    Pending = 1,

    /// <summary>The order exists at the CA (its URL is stored); authorizations and challenges are in progress.</summary>
    Ordered = 2,

    /// <summary>The vault staged a key and made the CSR (both stored), and the CA is finalizing.</summary>
    Finalizing = 3,

    /// <summary>The last order finished: the chain is attached and the certificate is registered. The row now holds the renewal schedule.</summary>
    Valid = 4,

    /// <summary>The last attempt failed (see the error); the scheduler retries at <c>RetryAt</c>. An order that is still usable at the CA is resumed.</summary>
    Failed = 5,
}

/// <summary>The ACME challenge type a certificate's names are proven with. Persisted by name (<see cref="ModelNames"/>), the same as in RFC 8555.</summary>
public enum AcmeChallengeType
{
    /// <summary>HTTP-01 (RFC 8555 §8.3): served by the public listener's gate. Not for wildcards.</summary>
    Http01 = 1,

    /// <summary>DNS-01 (RFC 8555 §8.4): a TXT record through a DNS provider. Not for IP addresses.</summary>
    Dns01 = 2,
}

/// <summary>The DNS services Paduraksa can drive for DNS-01. Persisted by name (<see cref="ModelNames"/>); adding a kind needs a migration for the column's check.</summary>
public enum DnsProviderKind
{
    /// <summary>Cloudflare's API.</summary>
    Cloudflare = 1,

    /// <summary>deSEC's API.</summary>
    DeSec = 2,
}
