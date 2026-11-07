using Paduraksa.Core.Vault;

namespace Paduraksa.Acme.Dns;

/// <summary>Tunables of the HTTP DNS providers (Cloudflare, deSEC). The defaults suit the real APIs. A record, so a variation is <c>options with { ... }</c>.</summary>
public sealed record DnsProviderOptions
{
    /// <summary>The longest one API call may take, headers and body.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The largest response body accepted, in bytes. A page of zones or records is a few kilobytes.</summary>
    public int MaxResponseBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// A rate limit (429) whose <c>Retry-After</c> is at most this is waited out once inside the call, because a TXT record
    /// is written and read back in a burst of requests; a longer one is thrown as a rate-limited <see cref="DnsProviderException"/>
    /// carrying the hint. <see cref="TimeSpan.Zero"/> never waits. The default covers deSEC's per-domain write limit, whose
    /// <c>Retry-After</c> is up to a minute.
    /// </summary>
    public TimeSpan MaxInlineRetryAfter { get; init; } = TimeSpan.FromSeconds(65);

    /// <summary>The TTL of the challenge record, in seconds, where the provider allows it. Cloudflare's minimum is 60; deSEC raises it to the domain's minimum (3600 by default).</summary>
    public int RecordTtlSeconds { get; init; } = 60;

    /// <summary>Connection settings of the vault-built transport. Timeouts, connection lifetime and an optional proxy; nothing here can see the credential.</summary>
    public SecretHttpTransportOptions Transport { get; init; } = SecretHttpTransportOptions.Default;

    /// <summary>The <c>User-Agent</c> product token.</summary>
    public string UserAgent { get; init; } = "Paduraksa-Dns/1.0";
}
