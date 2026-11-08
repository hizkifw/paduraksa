using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Acme;

/// <summary>
/// An RFC 8555 client for one CA directory (PLAN §9). It is the protocol layer only: it holds no key (the account key
/// signs through <see cref="IJwsSigner"/>, the EAB key MACs through <see cref="IMacKeySource"/>), no state beyond a cached
/// directory and a nonce pool, and it neither schedules, persists nor serves challenges. Callers keep the account URL
/// (<see cref="AcmeAccountRef"/>) and the order URLs. Thread-safe; build one per CA and share it. Dispose it with the
/// application: it owns the <see cref="HttpClient"/> it builds.
/// </summary>
/// <remarks>
/// <para>
/// Failures are <see cref="AcmeException"/>s whose <see cref="AcmeException.Kind"/> tells a scheduler whether to retry. Caller
/// mistakes (an account without a key, a malformed ID) are <see cref="InvalidOperationException"/> and
/// <see cref="ArgumentException"/>. The <c>WaitFor...</c> methods return the resource in whatever terminal state it reached,
/// <see cref="AcmeStatus.Invalid"/> included, so a caller can read the embedded problem; they throw only for a failed request
/// or an <see cref="AcmePollException"/> when the CA stays busy or asks to be left alone longer than the poll bounds.
/// </para>
/// <para>
/// Every URL the client requests or accepts must be on the directory's origin (<see cref="AcmeClientOptions.AdditionalOrigins"/>
/// adds more). The single-shot calls (<c>GetOrderAsync</c>, <c>GetAuthorizationAsync</c>, <c>RespondToChallengeAsync</c>,
/// <c>FinalizeOrderAsync</c>) put the CA's <c>Retry-After</c> on the model they return, for a scheduler that waits on its own.
/// </para>
/// </remarks>
public sealed class AcmeClient : IDisposable
{
    private const string PemChainContentType = "application/pem-certificate-chain";

    private readonly HttpClient _http;
    private readonly AcmeTransport _transport;
    private readonly IMacKeySource? _macKeys;
    private readonly AcmeClientOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    /// <param name="directoryUrl">The CA's directory URL.</param>
    /// <param name="signer">Signs account-key JWS and reports the public key.</param>
    /// <param name="macKeys">Opens EAB MAC keys; may be <see langword="null"/> if no account uses external account binding.</param>
    /// <param name="options">Timeouts, polling bounds and allowed origins; defaults if <see langword="null"/>.</param>
    /// <param name="handler">
    /// The transport, <see cref="AcmeHttp.CreateHandler()"/> if <see langword="null"/>. The client builds its own
    /// <see cref="HttpClient"/> on it, so no caller-added layer wraps it. A handler passed in must not follow redirects and
    /// must not retry (a retry replays a signed body with a used nonce); it is not disposed by the client.
    /// </param>
    /// <param name="time">The clock for timeouts, polling delays and <c>Retry-After</c> dates; the system clock if <see langword="null"/>.</param>
    /// <param name="logger">Receives URLs and problem types, never bodies.</param>
    public AcmeClient(
        Uri directoryUrl,
        IJwsSigner signer,
        IMacKeySource? macKeys = null,
        AcmeClientOptions? options = null,
        HttpMessageHandler? handler = null,
        TimeProvider? time = null,
        ILogger<AcmeClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(directoryUrl);
        ArgumentNullException.ThrowIfNull(signer);
        _macKeys = macKeys;
        _options = options ?? new AcmeClientOptions();
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<AcmeClient>.Instance;

        // The per-request deadline is ours (AcmeClientOptions.RequestTimeout), not HttpClient's shared one.
        _http = new HttpClient(handler ?? AcmeHttp.CreateHandler(), disposeHandler: handler is null) { Timeout = Timeout.InfiniteTimeSpan };
        _transport = new AcmeTransport(_http, directoryUrl, signer, _options, _time, _logger);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>The directory, fetched once and cached for <see cref="AcmeClientOptions.DirectoryCacheDuration"/>. <c>Meta</c> carries the terms of service, EAB requirement and profiles.</summary>
    public Task<AcmeDirectory> GetDirectoryAsync(CancellationToken cancellationToken) =>
        _transport.GetDirectoryAsync(cancellationToken);

    // ---- Accounts (RFC 8555 §7.3) ----

    /// <summary>
    /// Registers the account whose key is already in the vault (<see cref="IJwsSigner.CreateAccountKeyAsync"/>). If the CA
    /// already knows the key it returns that account instead (<see cref="AcmeAccount.Created"/> is false). With
    /// <see cref="AcmeNewAccountRequest.ExternalAccount"/> the request carries an HS256 (or HS384, HS512 as the MAC key's
    /// algorithm dictates) inner JWS over the account JWK, MACed by the vault (§7.3.4).
    /// </summary>
    public async Task<AcmeAccount> RegisterAccountAsync(AcmeAccountId id, AcmeNewAccountRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var directory = await GetDirectoryAsync(cancellationToken);
        JwsFlattened? binding = null;
        if (request.ExternalAccount is { } eab)
        {
            var mac = _macKeys is null
                ? throw new InvalidOperationException("External account binding needs an IMacKeySource.")
                : await _macKeys.OpenAsync(eab.MacKeySecret, SecretKind.EabHmacKey, cancellationToken)
                    ?? throw new InvalidOperationException($"Secret {eab.MacKeySecret} is not an EAB HMAC key in the vault.");
            var key = await _transport.GetKeyAsync(id, cancellationToken);
            binding = AcmeJws.ExternalAccountBinding(mac, eab.KeyId, directory.NewAccount, key.Jwk);
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new NewAccountWire
            {
                Contact = request.Contacts.Count > 0 ? [.. request.Contacts] : null,
                TermsOfServiceAgreed = request.TermsOfServiceAgreed ? true : null,
                ExternalAccountBinding = binding,
            },
            AcmeJsonContext.Default.NewAccountWire);
        var response = await _transport.PostAsync(id, null, directory.NewAccount, payload, null, cancellationToken);
        return response.Read(AcmeJsonContext.Default.AccountWire)
            .ToModel(id, response.RequireLocation(), response.Status == System.Net.HttpStatusCode.Created);
    }

    /// <summary>The account the CA holds for this key, or <see langword="null"/> if there is none (<c>onlyReturnExisting</c>, §7.3.1).</summary>
    public async Task<AcmeAccount?> FindAccountAsync(AcmeAccountId id, CancellationToken cancellationToken)
    {
        var directory = await GetDirectoryAsync(cancellationToken);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new NewAccountWire { OnlyReturnExisting = true }, AcmeJsonContext.Default.NewAccountWire);
        try
        {
            var response = await _transport.PostAsync(id, null, directory.NewAccount, payload, null, cancellationToken);
            return response.Read(AcmeJsonContext.Default.AccountWire).ToModel(id, response.RequireLocation(), created: false);
        }
        catch (AcmeProblemException ex) when (ex.Type == AcmeProblemTypes.AccountDoesNotExist)
        {
            return null;
        }
    }

    /// <summary>Reads the account (POST-as-GET on its URL).</summary>
    public async Task<AcmeAccount> GetAccountAsync(AcmeAccountRef account, CancellationToken cancellationToken)
    {
        var response = await PostAsync(account, account.Url, ReadOnlyMemory<byte>.Empty, null, cancellationToken);
        return response.Read(AcmeJsonContext.Default.AccountWire).ToModel(account.Id, account.Url, created: false);
    }

    /// <summary>Replaces the account's contact URIs (§7.3.2).</summary>
    public async Task<AcmeAccount> UpdateContactsAsync(AcmeAccountRef account, IReadOnlyList<string> contacts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contacts);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new ContactUpdateWire { Contact = [.. contacts] }, AcmeJsonContext.Default.ContactUpdateWire);
        var response = await PostAsync(account, account.Url, payload, null, cancellationToken);
        return response.Read(AcmeJsonContext.Default.AccountWire).ToModel(account.Id, account.Url, created: false);
    }

    /// <summary>Deactivates the account (§7.3.6). The CA does not reactivate it.</summary>
    public async Task<AcmeAccount> DeactivateAccountAsync(AcmeAccountRef account, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new DeactivateWire(), AcmeJsonContext.Default.DeactivateWire);
        var response = await PostAsync(account, account.Url, payload, null, cancellationToken);
        return response.Read(AcmeJsonContext.Default.AccountWire).ToModel(account.Id, account.Url, created: false);
    }

    // ---- Orders and authorizations (RFC 8555 §7.4, §7.5) ----

    /// <summary>Places an order (§7.4). <see cref="AcmeNewOrderRequest.Replaces"/> marks it as the renewal of an ARI-identified certificate (RFC 9773 §5).</summary>
    public async Task<AcmeOrder> CreateOrderAsync(AcmeAccountRef account, AcmeNewOrderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Identifiers.Count == 0)
        {
            throw new ArgumentException("An order needs at least one identifier.", nameof(request));
        }

        if (request.Replaces is not null && !AcmeRenewalInfoId.IsValid(request.Replaces))
        {
            throw new ArgumentException("Replaces must be an ARI certificate ID (base64url '.' base64url).", nameof(request));
        }

        var directory = await GetDirectoryAsync(cancellationToken);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new NewOrderWire { Identifiers = [.. request.Identifiers], Profile = request.Profile, Replaces = request.Replaces },
            AcmeJsonContext.Default.NewOrderWire);
        var response = await PostAsync(account, directory.NewOrder, payload, null, cancellationToken);
        return response.Read(AcmeJsonContext.Default.OrderWire).ToModel(response.RequireLocation(), response.RetryAfter);
    }

    public async Task<AcmeOrder> GetOrderAsync(AcmeAccountRef account, Uri orderUrl, CancellationToken cancellationToken)
    {
        var response = await PostAsync(account, orderUrl, ReadOnlyMemory<byte>.Empty, null, cancellationToken);
        return response.Read(AcmeJsonContext.Default.OrderWire).ToModel(orderUrl, response.RetryAfter);
    }

    public async Task<AcmeAuthorization> GetAuthorizationAsync(AcmeAccountRef account, Uri authorizationUrl, CancellationToken cancellationToken)
    {
        var response = await PostAsync(account, authorizationUrl, ReadOnlyMemory<byte>.Empty, null, cancellationToken);
        return response.Read(AcmeJsonContext.Default.AuthorizationWire).ToModel(authorizationUrl, response.RetryAfter);
    }

    /// <summary>Deactivates an authorization the account no longer wants (§7.5.2), e.g. after a failed order.</summary>
    public async Task<AcmeAuthorization> DeactivateAuthorizationAsync(AcmeAccountRef account, Uri authorizationUrl, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new DeactivateWire(), AcmeJsonContext.Default.DeactivateWire);
        var response = await PostAsync(account, authorizationUrl, payload, null, cancellationToken);
        return response.Read(AcmeJsonContext.Default.AuthorizationWire).ToModel(authorizationUrl, response.RetryAfter);
    }

    // ---- Challenges (RFC 8555 §7.5.1, §8) ----

    /// <summary>
    /// The key authorization for <paramref name="challenge"/>: its token and the account key's thumbprint, which the vault
    /// reports. Serve it for HTTP-01, or pass it to <see cref="AcmeChallenges.Dns01TxtValue"/> for DNS-01.
    /// </summary>
    public async Task<string> GetKeyAuthorizationAsync(AcmeAccountId id, AcmeChallenge challenge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        var key = await _transport.GetKeyAsync(id, cancellationToken);
        return AcmeChallenges.KeyAuthorization(
            challenge.Token ?? throw new ArgumentException("The challenge has no token.", nameof(challenge)),
            key.Thumbprint);
    }

    /// <summary>Tells the CA the challenge response is in place and it may validate (§7.5.1). Poll the authorization afterwards.</summary>
    public async Task<AcmeChallenge> RespondToChallengeAsync(AcmeAccountRef account, AcmeChallenge challenge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        var response = await PostAsync(account, challenge.Url, "{}"u8.ToArray(), null, cancellationToken);
        return response.Read(AcmeJsonContext.Default.ChallengeWire).ToModel(response.RetryAfter);
    }

    /// <summary>
    /// Polls the authorization while it is <see cref="AcmeStatus.Pending"/>, honouring <c>Retry-After</c> within the configured
    /// bounds (see <see cref="AcmePollException"/>).
    /// </summary>
    public Task<AcmeAuthorization> WaitForAuthorizationAsync(AcmeAccountRef account, Uri authorizationUrl, CancellationToken cancellationToken) =>
        PollAsync(
            authorizationUrl,
            ct => GetAuthorizationAsync(account, authorizationUrl, ct),
            static a => a.Status,
            static a => a.RetryAfter,
            static status => status is not AcmeStatus.Pending,
            null,
            cancellationToken);

    /// <summary>Polls the order until it leaves <see cref="AcmeStatus.Pending"/> and <see cref="AcmeStatus.Processing"/>: it is Ready for finalization, or already Valid or Invalid.</summary>
    public Task<AcmeOrder> WaitForOrderReadyAsync(AcmeAccountRef account, Uri orderUrl, CancellationToken cancellationToken) =>
        PollAsync(
            orderUrl,
            ct => GetOrderAsync(account, orderUrl, ct),
            static o => o.Status,
            static o => o.RetryAfter,
            static status => status is not (AcmeStatus.Pending or AcmeStatus.Processing),
            null,
            cancellationToken);

    /// <summary>
    /// Polls a finalized order until it is Valid (the certificate URL is set) or Invalid. An order that is Ready (never
    /// finalized) or of an unknown status will not get there by waiting, so that fails at once with an
    /// <see cref="AcmeProtocolException"/>.
    /// </summary>
    public Task<AcmeOrder> WaitForOrderIssuedAsync(AcmeAccountRef account, Uri orderUrl, CancellationToken cancellationToken) =>
        PollAsync(
            orderUrl,
            ct => GetOrderAsync(account, orderUrl, ct),
            static o => o.Status,
            static o => o.RetryAfter,
            static status => status is AcmeStatus.Valid or AcmeStatus.Invalid,
            static o =>
            {
                if (o.Status is AcmeStatus.Ready or AcmeStatus.Unknown)
                {
                    throw new AcmeProtocolException($"{AcmeText.Url(o.Url)} is {o.Status} after finalization; it will not become valid by waiting.");
                }
            },
            cancellationToken);

    // ---- Finalization and download (RFC 8555 §7.4, §7.4.2) ----

    /// <summary>Submits the CSR (<see cref="CsrResult.Der"/> from the vault) for a <see cref="AcmeStatus.Ready"/> order. The answer is Processing or Valid; poll with <see cref="WaitForOrderIssuedAsync"/>.</summary>
    public async Task<AcmeOrder> FinalizeOrderAsync(AcmeAccountRef account, AcmeOrder order, ReadOnlyMemory<byte> csrDer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (csrDer.IsEmpty)
        {
            throw new ArgumentException("The CSR is empty.", nameof(csrDer));
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new FinalizeWire { Csr = Base64Url.EncodeToString(csrDer.Span) }, AcmeJsonContext.Default.FinalizeWire);
        var response = await PostAsync(account, order.Finalize, payload, null, cancellationToken);
        return response.Read(AcmeJsonContext.Default.OrderWire).ToModel(order.Url, response.RetryAfter);
    }

    /// <summary>
    /// Downloads the certificate chain (POST-as-GET, <c>application/pem-certificate-chain</c>). With
    /// <paramref name="preferredIssuerCommonName"/> set, and the default chain not ending in a certificate issued by an issuer
    /// of that name, the <c>rel="alternate"</c> chains are tried in order and the first match is returned; with no match
    /// the default chain is returned. Only the topmost certificate's issuer counts, as certbot and lego match
    /// <c>preferred_chain</c>: in [leaf, E5, X2 cross-signed by X1] the top issuer is "ISRG Root X1", although E5's issuer
    /// is "ISRG Root X2". The alternates of the returned chain's own response are in <see cref="AcmeCertificateDownload.Alternates"/>.
    /// Every certificate is parsed, and the returned PEM is rebuilt from the certificate blocks alone.
    /// </summary>
    public async Task<AcmeCertificateDownload> DownloadCertificateAsync(
        AcmeAccountRef account, Uri certificateUrl, string? preferredIssuerCommonName, CancellationToken cancellationToken)
    {
        var first = await DownloadChainAsync(account, certificateUrl, cancellationToken);
        if (preferredIssuerCommonName is null || MatchesTopIssuer(first, preferredIssuerCommonName))
        {
            return first;
        }

        foreach (var alternate in first.Alternates)
        {
            var candidate = await DownloadChainAsync(account, alternate, cancellationToken);
            if (MatchesTopIssuer(candidate, preferredIssuerCommonName))
            {
                return candidate;
            }
        }

        return first;
    }

    // ---- ARI (RFC 9773) ----

    /// <summary>
    /// The CA's renewal suggestion for the certificate named by <paramref name="certificateId"/> (<see cref="AcmeRenewalInfoId"/>),
    /// an unauthenticated GET. <see langword="null"/> if the CA does not offer ARI or does not know the certificate (404).
    /// </summary>
    public async Task<AcmeRenewalInfo?> GetRenewalInfoAsync(string certificateId, CancellationToken cancellationToken)
    {
        if (!AcmeRenewalInfoId.IsValid(certificateId))
        {
            throw new ArgumentException("Not an ARI certificate ID (base64url '.' base64url).", nameof(certificateId));
        }

        var directory = await GetDirectoryAsync(cancellationToken);
        if (directory.RenewalInfo is null)
        {
            return null;
        }

        AcmeResponse response;
        try
        {
            response = await _transport.GetAsync(
                new Uri($"{directory.RenewalInfo.OriginalString.TrimEnd('/')}/{certificateId}"), "application/json", cancellationToken);
        }
        catch (AcmeProblemException ex) when (ex.Status == 404)
        {
            return null;
        }

        var wire = response.Read(AcmeJsonContext.Default.RenewalInfoWire);
        if (wire.SuggestedWindow is not { Start: { } start, End: { } end } || end < start)
        {
            throw new AcmeProtocolException("The renewalInfo answer has no usable suggestedWindow.");
        }

        // The explanation is shown to an operator, never requested: only an absolute http(s) link is kept.
        return new AcmeRenewalInfo(start, end, AcmeText.IsDisplayUrl(wire.ExplanationUrl) ? wire.ExplanationUrl : null, response.RetryAfter);
    }

    /// <summary>As <see cref="GetRenewalInfoAsync(string, CancellationToken)"/> for a certificate the vault reported; <see langword="null"/> also if it has no authority key identifier.</summary>
    public Task<AcmeRenewalInfo?> GetRenewalInfoAsync(CertificateInfo certificate, CancellationToken cancellationToken) =>
        AcmeRenewalInfoId.TryCreate(certificate, out var id)
            ? GetRenewalInfoAsync(id, cancellationToken)
            : Task.FromResult<AcmeRenewalInfo?>(null);

    // ---- Revocation (RFC 8555 §7.6) ----

    /// <summary>Revokes a certificate with the account key (signed with <c>kid</c>, like every request but newAccount). <paramref name="certificateDer"/> is the leaf, e.g. <see cref="AcmePem.LeafDer"/>.</summary>
    public async Task RevokeCertificateAsync(
        AcmeAccountRef account, ReadOnlyMemory<byte> certificateDer, AcmeRevocationReason? reason, CancellationToken cancellationToken)
    {
        if (certificateDer.IsEmpty)
        {
            throw new ArgumentException("The certificate is empty.", nameof(certificateDer));
        }

        var directory = await GetDirectoryAsync(cancellationToken);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new RevokeWire { Certificate = Base64Url.EncodeToString(certificateDer.Span), Reason = (int?)reason },
            AcmeJsonContext.Default.RevokeWire);
        await PostAsync(account, directory.RevokeCert, payload, null, cancellationToken);
    }

    // ---- Internals ----

    /// <summary>A signed request with the account's <c>kid</c>; an unset <c>default(AcmeAccountRef)</c> is a caller bug.</summary>
    private Task<AcmeResponse> PostAsync(
        AcmeAccountRef account, Uri url, ReadOnlyMemory<byte> payload, string? accept, CancellationToken cancellationToken)
    {
        if (account.Url is null)
        {
            throw new ArgumentException("The account reference is not set.", nameof(account));
        }

        ArgumentNullException.ThrowIfNull(url);
        return _transport.PostAsync(account.Id, account.Url, url, payload, accept, cancellationToken);
    }

    private async Task<AcmeCertificateDownload> DownloadChainAsync(AcmeAccountRef account, Uri url, CancellationToken cancellationToken)
    {
        var response = await PostAsync(account, url, ReadOnlyMemory<byte>.Empty, PemChainContentType, cancellationToken);
        try
        {
            var (pem, issuers) = AcmePem.NormalizeChain(Encoding.UTF8.GetString(response.Body));
            return new AcmeCertificateDownload(pem, url, response.Alternates, issuers);
        }
        catch (FormatException ex)
        {
            throw new AcmeProtocolException($"{AcmeText.Url(url)} did not return a valid PEM certificate chain.", ex);
        }
    }

    private static bool MatchesTopIssuer(AcmeCertificateDownload chain, string issuerCommonName) =>
        chain.IssuerCommonNames.Count > 0
        && string.Equals(chain.IssuerCommonNames[^1], issuerCommonName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fetches until <paramref name="done"/>. Between polls it waits the CA's <c>Retry-After</c>, else
    /// <see cref="AcmeClientOptions.PollInterval"/>, kept at least <see cref="AcmeClientOptions.MinPollDelay"/>. A
    /// <c>Retry-After</c> above <see cref="AcmeClientOptions.MaxPollDelay"/> ends the poll (polling earlier would ignore the
    /// CA, waiting would block the caller), and so does <see cref="AcmeClientOptions.PollTimeout"/>; both throw an
    /// <see cref="AcmePollException"/> that holds the last copy of the resource.
    /// </summary>
    private async Task<T> PollAsync<T>(
        Uri url,
        Func<CancellationToken, Task<T>> fetch,
        Func<T, AcmeStatus> statusOf,
        Func<T, TimeSpan?> retryAfterOf,
        Func<AcmeStatus, bool> done,
        Action<T>? guard,
        CancellationToken cancellationToken)
        where T : class
    {
        var started = _time.GetTimestamp();
        while (true)
        {
            var value = await fetch(cancellationToken);
            guard?.Invoke(value);
            var status = statusOf(value);
            if (done(status))
            {
                return value;
            }

            var retryAfter = retryAfterOf(value);
            if (retryAfter > _options.MaxPollDelay)
            {
                throw new AcmePollException(
                    $"{AcmeText.Url(url)} is {status} and the CA asked to wait {retryAfter}, longer than the {_options.MaxPollDelay} polling bound.",
                    AcmePollFailure.RetryAfterTooLong, url, status, value, retryAfter);
            }

            var delay = retryAfter ?? _options.PollInterval;
            delay = delay < _options.MinPollDelay ? _options.MinPollDelay : delay > _options.MaxPollDelay ? _options.MaxPollDelay : delay;
            if (_time.GetElapsedTime(started) + delay > _options.PollTimeout)
            {
                throw new AcmePollException(
                    $"{AcmeText.Url(url)} was still {status} after {_options.PollTimeout}.",
                    AcmePollFailure.Timeout, url, status, value, retryAfter);
            }

            AcmeLog.Polling(_logger, url, status, delay);
            await Task.Delay(delay, _time, cancellationToken);
        }
    }
}
