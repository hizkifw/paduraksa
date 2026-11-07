using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Acme;

/// <summary>What the CA answered, with the headers ACME cares about already read.</summary>
internal sealed record AcmeResponse(
    Uri Url,
    HttpStatusCode Status,
    byte[] Body,
    Uri? Location,
    TimeSpan? RetryAfter,
    IReadOnlyList<Uri> Alternates,
    string? ReplayNonce)
{
    /// <summary>Deserializes the body, turning bad JSON into an <see cref="AcmeProtocolException"/>.</summary>
    public T Read<T>(JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(Body, typeInfo) ?? throw new AcmeProtocolException($"{AcmeText.Url(Url)} answered with null.");
        }
        catch (JsonException ex)
        {
            throw new AcmeProtocolException($"{AcmeText.Url(Url)} answered with a body that is not the expected JSON.", ex);
        }
    }

    public Uri RequireLocation() =>
        Location ?? throw new AcmeProtocolException($"{AcmeText.Url(Url)} answered without a Location header.");
}

/// <summary>
/// The HTTP and JWS plumbing: directory cache, nonce pool, signed POSTs with <c>badNonce</c> retry, problem parsing,
/// origin and redirect checks, bounded bodies and per-request timeouts.
/// </summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The SemaphoreSlim never touches its wait handle, so there is nothing to release.")]
internal sealed partial class AcmeTransport
{
    private const int MaxPooledNonces = 32;
    private const string JoseContentType = "application/jose+json";

    private readonly HttpClient _http;
    private readonly Uri _directoryUrl;
    private readonly IJwsSigner _signer;
    private readonly AcmeClientOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly HashSet<string> _origins;
    private readonly List<string> _nonces = [];
    private readonly Lock _nonceGate = new();
    private readonly SemaphoreSlim _directoryGate = new(1, 1);
    private CachedDirectory? _directory;

    public AcmeTransport(HttpClient http, Uri directoryUrl, IJwsSigner signer, AcmeClientOptions options, TimeProvider time, ILogger logger)
    {
        _http = http;
        _directoryUrl = directoryUrl;
        _signer = signer;
        _options = options;
        _time = time;
        _logger = logger;
        _origins = [OriginOf(directoryUrl), .. options.AdditionalOrigins.Select(OriginOf)];
        RequireAllowed(directoryUrl);
    }

    private sealed record CachedDirectory(AcmeDirectory Directory, long Timestamp);

    public async Task<AcmeDirectory> GetDirectoryAsync(CancellationToken cancellationToken)
    {
        if (Fresh(_directory) is { } cached)
        {
            return cached;
        }

        await _directoryGate.WaitAsync(cancellationToken);
        try
        {
            if (Fresh(_directory) is { } again)
            {
                return again;
            }

            var response = await ExecuteAsync(HttpMethod.Get, _directoryUrl, null, "application/json", true, cancellationToken);
            var directory = Validate(response.Read(AcmeJsonContext.Default.AcmeDirectory));
            _directory = new CachedDirectory(directory, _time.GetTimestamp());
            AcmeLog.DirectoryFetched(_logger, _directoryUrl);
            return directory;
        }
        finally
        {
            _directoryGate.Release();
        }
    }

    /// <summary>An unauthenticated GET (the directory is fetched through this class itself; ARI uses this).</summary>
    public Task<AcmeResponse> GetAsync(Uri url, string? accept, CancellationToken cancellationToken) =>
        ExecuteAsync(HttpMethod.Get, url, null, accept, true, cancellationToken);

    /// <summary>
    /// A signed POST (RFC 8555 §6.2). <paramref name="kid"/> is the account URL, or <see langword="null"/> to sign with a
    /// <c>jwk</c> header (<c>newAccount</c>). An empty <paramref name="payload"/> is POST-as-GET (§6.3). A <c>badNonce</c>
    /// answer is retried with the fresh nonce it carries, signing the request again.
    /// </summary>
    public async Task<AcmeResponse> PostAsync(
        AcmeAccountId id, Uri? kid, Uri url, ReadOnlyMemory<byte> payload, string? accept, CancellationToken cancellationToken)
    {
        var directory = await GetDirectoryAsync(cancellationToken);
        var key = await GetKeyAsync(id, cancellationToken);
        RequireAllowed(url);
        for (var attempt = 1; ; attempt++)
        {
            var nonce = await TakeNonceAsync(directory.NewNonce, cancellationToken);
            var header = AcmeJws.ProtectedHeader(key.Algorithm, kid is null ? key.Jwk : null, kid, nonce, url);
            var jws = await _signer.SignAsync(id, header, payload, cancellationToken);
            var body = JsonSerializer.SerializeToUtf8Bytes(jws, AcmeJsonContext.Default.JwsFlattened);
            try
            {
                return await ExecuteAsync(HttpMethod.Post, url, body, accept, true, cancellationToken);
            }
            catch (AcmeProblemException ex) when (ex.Type == AcmeProblemTypes.BadNonce)
            {
                if (attempt > _options.MaxBadNonceRetries)
                {
                    AcmeLog.BadNonceExhausted(_logger, url, attempt);
                    throw;
                }

                AcmeLog.BadNonceRetry(_logger, url, attempt);
            }
        }
    }

    public async Task<AcmeAccountKeyInfo> GetKeyAsync(AcmeAccountId id, CancellationToken cancellationToken) =>
        await _signer.GetAccountKeyAsync(id, cancellationToken)
        ?? throw new InvalidOperationException($"ACME account {id} has no key in the vault.");

    private AcmeDirectory? Fresh(CachedDirectory? cached) =>
        cached is not null && _time.GetElapsedTime(cached.Timestamp) < _options.DirectoryCacheDuration ? cached.Directory : null;

    private AcmeDirectory Validate(AcmeDirectory directory)
    {
        // The deserializer leaves a missing member null, whatever the declared type says.
        Uri?[] urls = [directory.NewNonce, directory.NewAccount, directory.NewOrder, directory.RevokeCert, directory.NewAuthz, directory.KeyChange, directory.RenewalInfo];
        if (urls.Take(4).Any(static u => u is null))
        {
            throw new AcmeProtocolException("The directory lacks newNonce, newAccount, newOrder or revokeCert.");
        }

        foreach (var url in urls.OfType<Uri>())
        {
            RequireAllowed(url);
        }

        // Links in meta are shown to an operator, never requested: keep them only if they are absolute http(s).
        return directory.Meta is { } meta
            ? directory with
            {
                Meta = meta with
                {
                    TermsOfService = AcmeText.IsDisplayUrl(meta.TermsOfService) ? meta.TermsOfService : null,
                    Website = AcmeText.IsDisplayUrl(meta.Website) ? meta.Website : null,
                },
            }
            : directory;
    }

    private async Task<string> TakeNonceAsync(Uri newNonceUrl, CancellationToken cancellationToken)
    {
        lock (_nonceGate)
        {
            if (_nonces.Count > 0)
            {
                var nonce = _nonces[^1];
                _nonces.RemoveAt(_nonces.Count - 1);
                return nonce;
            }
        }

        // The nonce a HEAD returns goes straight to the caller that asked: it never waits in the pool for another to take.
        var response = await ExecuteAsync(HttpMethod.Head, newNonceUrl, null, null, false, cancellationToken);
        return response.ReplayNonce
            ?? throw new AcmeProtocolException($"{AcmeText.Url(newNonceUrl)} did not return a usable Replay-Nonce.");
    }

    private static string? ReadNonce(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Replay-Nonce", out var values) && values.FirstOrDefault() is { } nonce && AcmeChallenges.IsValidToken(nonce)
            ? nonce
            : null;

    private void PoolNonce(string? nonce, bool pool)
    {
        if (nonce is null || !pool)
        {
            return;
        }

        lock (_nonceGate)
        {
            if (_nonces.Count >= MaxPooledNonces)
            {
                _nonces.RemoveAt(0);
            }

            _nonces.Add(nonce);
        }
    }

    private async Task<AcmeResponse> ExecuteAsync(
        HttpMethod method, Uri url, byte[]? body, string? accept, bool poolNonce, CancellationToken cancellationToken)
    {
        RequireAllowed(url);
        var loggedUrl = AcmeText.Url(url);
        AcmeLog.Request(_logger, method.Method, loggedUrl);

        using var deadline = new CancellationTokenSource(_options.RequestTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.ParseAdd(_options.UserAgent);
        request.Headers.AcceptLanguage.ParseAdd("en");
        if (accept is not null)
        {
            request.Headers.Accept.ParseAdd(accept);
        }

        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(JoseContentType);
        }

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);

            // A signed request is bound to its URL, so it is never resent anywhere else: a redirect is an error, whether
            // the handler surfaced it (3xx) or followed it (the response's request is not the one sent). Nothing a
            // rejected response carries is trusted, its nonce included.
            if ((int)response.StatusCode is >= 300 and < 400
                || (response.RequestMessage?.RequestUri is { } final && final != url))
            {
                throw new AcmeProtocolException($"{loggedUrl} redirected; ACME requests are bound to their URL and are not followed to another.");
            }

            var nonce = ReadNonce(response);
            var content = await ReadBodyAsync(response.Content, linked.Token);
            var retryAfter = ParseRetryAfter(response.Headers.RetryAfter);
            if (!response.IsSuccessStatusCode)
            {
                PoolNonce(nonce, poolNonce);
                var problem = ParseProblem(content, (int)response.StatusCode);
                var loggedType = AcmeText.Sanitize(problem.Type, 200);
                if (problem.Type == AcmeProblemTypes.BadNonce)
                {
                    AcmeLog.ProblemDebug(_logger, loggedUrl, loggedType, (int)response.StatusCode);
                }
                else
                {
                    AcmeLog.Problem(_logger, loggedUrl, loggedType, (int)response.StatusCode);
                }

                var terms = ParseLinks(url, response.Headers, "terms-of-service").FirstOrDefault(AcmeText.IsDisplayUrl);
                throw new AcmeProblemException(problem, url, retryAfter, terms);
            }

            var alternates = ParseLinks(url, response.Headers, "alternate");
            foreach (var alternate in alternates)
            {
                RequireAllowed(alternate);
            }

            var location = ResolveLocation(url, response.Headers.Location);
            PoolNonce(nonce, poolNonce);
            return new AcmeResponse(url, response.StatusCode, content, location, retryAfter, alternates, nonce);
        }
        catch (HttpRequestException ex)
        {
            throw new AcmeTransportException($"{loggedUrl} could not be reached: {AcmeText.Sanitize(ex.Message, 200)}", ex);
        }
        catch (IOException ex)
        {
            throw new AcmeTransportException($"{loggedUrl} broke off while it was being read: {AcmeText.Sanitize(ex.Message, 200)}", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AcmeTransportException($"{loggedUrl} did not answer within {_options.RequestTimeout}.", ex);
        }
    }

    private async Task<byte[]> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var limit = _options.MaxResponseBytes;
        if (content.Headers.ContentLength > limit)
        {
            throw new AcmeProtocolException($"The CA's response is larger than {limit} bytes.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                throw new AcmeProtocolException($"The CA's response is larger than {limit} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static AcmeProblem ParseProblem(byte[] body, int status)
    {
        // A non-ACME answer (a proxy's HTML 502, an empty body) becomes an about:blank problem carrying only the status,
        // so its classification still follows the status.
        try
        {
            var problem = JsonSerializer.Deserialize(body, AcmeJsonContext.Default.ProblemWire)?.ToModel();
            if (problem is not null)
            {
                return problem.Status is null ? problem with { Status = status } : problem;
            }
        }
        catch (JsonException)
        {
        }

        return new AcmeProblem("about:blank", $"HTTP {status}", status, []);
    }

    private TimeSpan? ParseRetryAfter(RetryConditionHeaderValue? header)
    {
        if (header?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (header?.Date is { } date)
        {
            var remaining = date - _time.GetUtcNow();
            return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }

        return null;
    }

    private Uri? ResolveLocation(Uri requestUrl, Uri? location)
    {
        if (location is null)
        {
            return null;
        }

        var absolute = location.IsAbsoluteUri ? location : new Uri(requestUrl, location);
        RequireAllowed(absolute);
        return absolute;
    }

    /// <summary>The targets of the <c>Link</c> headers with the given <c>rel</c>, resolved against the request URL. Not checked: the caller decides what the links may be.</summary>
    private static List<Uri> ParseLinks(Uri requestUrl, HttpResponseHeaders headers, string rel)
    {
        var links = new List<Uri>();
        if (!headers.TryGetValues("Link", out var values))
        {
            return links;
        }

        foreach (var value in values)
        {
            foreach (Match link in LinkPattern().Matches(value))
            {
                var relMatch = RelPattern().Match(link.Groups[2].Value);
                if (relMatch.Success
                    && relMatch.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(rel, StringComparer.OrdinalIgnoreCase)
                    && Uri.TryCreate(requestUrl, link.Groups[1].Value, out var target))
                {
                    links.Add(target);
                }
            }
        }

        return links;
    }

    /// <summary>
    /// A URL the client may request or accept from the CA: absolute, https (http only if the options allow it) and on the
    /// directory's origin or one of <see cref="AcmeClientOptions.AdditionalOrigins"/>.
    /// </summary>
    private void RequireAllowed(Uri url)
    {
        if (!url.IsAbsoluteUri)
        {
            throw new AcmeProtocolException($"{AcmeText.Url(url)} is not an absolute URL.");
        }

        if (!(url.Scheme == Uri.UriSchemeHttps || (_options.AllowInsecureHttp && url.Scheme == Uri.UriSchemeHttp)))
        {
            throw new AcmeProtocolException($"{AcmeText.Url(url)} is not an https URL.");
        }

        if (!_origins.Contains(OriginOf(url)))
        {
            throw new AcmeProtocolException($"{AcmeText.Url(url)} is not on the CA's origin.");
        }
    }

    private static string OriginOf(Uri url) => $"{url.Scheme}://{url.IdnHost}:{url.Port}";

    [GeneratedRegex(@"<([^>]*)>((?:\s*;[^,<]*)*)", RegexOptions.CultureInvariant)]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@";\s*rel\s*=\s*""?([^"";]*)""?", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RelPattern();
}
