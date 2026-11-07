namespace Paduraksa.Acme;

/// <summary>Tunables of <see cref="AcmeClient"/>. The defaults suit a public CA. A record, so a variation is <c>options with { ... }</c>.</summary>
public sealed record AcmeClientOptions
{
    /// <summary>The longest one HTTP exchange may take, headers and body. Applied per request, on the client's <see cref="TimeProvider"/>.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How many times a request refused with <c>badNonce</c> is signed again with a fresh nonce (RFC 8555 §6.5) before the problem is thrown.</summary>
    public int MaxBadNonceRetries { get; init; } = 3;

    /// <summary>The delay between polls when the CA sent no <c>Retry-After</c>.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>The shortest delay between polls: a <c>Retry-After: 0</c> does not make the client spin.</summary>
    public TimeSpan MinPollDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest delay between polls: a larger <c>Retry-After</c> is shortened to this, so a polling call stays bounded.</summary>
    public TimeSpan MaxPollDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a <c>WaitFor…</c> call polls in all before it gives up with an <see cref="AcmeTransportException"/>.</summary>
    public TimeSpan PollTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a fetched directory is reused.</summary>
    public TimeSpan DirectoryCacheDuration { get; init; } = TimeSpan.FromHours(1);

    /// <summary>The largest response body accepted, in bytes. A directory, an order and a certificate chain are a few kilobytes.</summary>
    public int MaxResponseBytes { get; init; } = 1024 * 1024;

    /// <summary>The <c>User-Agent</c> product token (RFC 8555 §6.1 asks for one).</summary>
    public string UserAgent { get; init; } = "Paduraksa-Acme/1.0";

    /// <summary>
    /// Origins (scheme, host, port) besides the directory's that the CA may use. By default every URL the client requests or
    /// accepts from the CA (order and challenge URLs, <c>Location</c>, alternate chains, <c>renewalInfo</c>) must have the
    /// directory's origin, so a hostile or hijacked answer cannot make the client sign requests for another host. A CA that
    /// really serves from several hosts lists the extra ones here (only scheme, host and port are used).
    /// </summary>
    public IReadOnlyList<Uri> AdditionalOrigins { get; init; } = [];

    /// <summary>
    /// Accept <c>http://</c> URLs. Off, and meant to stay off outside tests: ACME runs over HTTPS (RFC 8555 §6.1), and
    /// every URL the CA hands out is checked against it.
    /// </summary>
    public bool AllowInsecureHttp { get; init; }
}

/// <summary>The HTTP handler an <see cref="AcmeClient"/> should run on.</summary>
public static class AcmeHttp
{
    /// <summary>
    /// The handler <see cref="AcmeClient"/> uses unless it is given one: it never follows redirects. A signed ACME request is
    /// bound to its URL (RFC 8555 §6.4), so a redirect would resend a body signed for another URL to a new location, where it
    /// is useless to the CA and visible to whoever answers. The client rejects a redirect it can see, either as a 3xx or as a
    /// response whose request URL differs; this handler makes sure it sees the 3xx. A handler passed in must not follow
    /// redirects and must not retry: a retry resends a signed body with a nonce the CA has already used.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
    };
}
