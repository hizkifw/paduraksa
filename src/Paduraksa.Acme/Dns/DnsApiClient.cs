using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;

namespace Paduraksa.Acme.Dns;

/// <summary>What a DNS API answered: status, the bounded body and the rate-limit hint.</summary>
internal sealed record DnsApiResponse(HttpStatusCode Status, byte[] Body, TimeSpan? RetryAfter)
{
    public bool IsSuccess => (int)Status is >= 200 and < 300;

    /// <summary>Deserializes the body; a body that is not the expected JSON is a protocol failure, not a transient one.</summary>
    public T Read<T>(JsonTypeInfo<T> typeInfo, string provider)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(Body, typeInfo) ?? throw new DnsProviderException($"{provider} answered with null.", isTransient: false);
        }
        catch (JsonException ex)
        {
            throw new DnsProviderException($"{provider} answered with a body that is not the expected JSON.", isTransient: false, ex);
        }
    }
}

/// <summary>
/// The HTTP plumbing of one provider operation: sends requests over a vault-built authenticating handler, bounds the body and
/// the time, and turns transport trouble and rate limits into <see cref="DnsProviderException"/>s. It never sees the credential:
/// the handler adds it inside the vault. Messages carry a status, a provider's own error text (sanitized) and the provider's
/// name, never a header.
/// </summary>
internal sealed partial class DnsApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _provider;
    private readonly DnsProviderOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public DnsApiClient(HttpMessageHandler handler, string provider, DnsProviderOptions options, TimeProvider time, ILogger logger)
    {
        _provider = provider;
        _options = options;
        _time = time;
        _logger = logger;
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Sends one request. Every status is returned, except a rate limit (429, after one short wait if the provider's hint is
    /// small) and a server error (5xx), which throw as transient: a caller never has to decide whether to try again on those.
    /// </summary>
    public async Task<DnsApiResponse> SendAsync(HttpMethod method, Uri url, byte[]? jsonBody, CancellationToken cancellationToken)
    {
        DnsApiResponse response = await SendOnceAsync(method, url, jsonBody, cancellationToken).ConfigureAwait(false);
        if (response.Status == HttpStatusCode.TooManyRequests
            && response.RetryAfter is { } wait && wait > TimeSpan.Zero && wait <= _options.MaxInlineRetryAfter)
        {
            LogThrottled(_provider, wait);
            await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
            response = await SendOnceAsync(method, url, jsonBody, cancellationToken).ConfigureAwait(false);
        }

        if (response.Status == HttpStatusCode.TooManyRequests)
        {
            string hint = response.RetryAfter is { } after ? $"; it asked to wait {Math.Ceiling(after.TotalSeconds)} seconds" : string.Empty;
            throw new DnsProviderException($"{_provider} is rate limiting this account{hint}.", isTransient: true) { RetryAfter = response.RetryAfter, IsRateLimited = true };
        }

        if ((int)response.Status >= 500)
        {
            throw new DnsProviderException($"{_provider} had a server error (HTTP {(int)response.Status}).", isTransient: true) { RetryAfter = response.RetryAfter };
        }

        return response;
    }

    public void Dispose() => _http.Dispose();

    /// <summary>The failure for an unexpected status: a refusal, which retrying does not fix (408 is the one client error that may pass).</summary>
    public DnsProviderException Failure(string action, DnsApiResponse response, string? providerText) =>
        new(
            $"{_provider} refused to {action} (HTTP {(int)response.Status}{(string.IsNullOrEmpty(providerText) ? string.Empty : ": " + providerText)}).",
            isTransient: response.Status == HttpStatusCode.RequestTimeout);

    /// <summary>A provider's error text as it goes into a message: no control characters, bounded.</summary>
    public static string Clean(string? text) => AcmeText.Sanitize(text, 200);

    /// <summary>The request body as JSON bytes.</summary>
    public static byte[] Json<T>(T value, JsonTypeInfo<T> typeInfo) => JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);

    private async Task<DnsApiResponse> SendOnceAsync(HttpMethod method, Uri url, byte[]? jsonBody, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(method, url);
        if (jsonBody is not null)
        {
            request.Content = new ByteArrayContent(jsonBody);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        // On the system clock, so a virtual clock in a test cannot cancel a call that is merely waiting for a server.
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.RequestTimeout);
        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            byte[] body = await ReadBoundedAsync(response, deadline.Token).ConfigureAwait(false);
            return new DnsApiResponse(response.StatusCode, body, RetryAfterOf(response));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DnsProviderException($"{_provider} did not answer within {_options.RequestTimeout.TotalSeconds:0} seconds.", isTransient: true);
        }
        catch (HttpRequestException ex)
        {
            // Only the error kind goes into the message.
            throw new DnsProviderException($"{_provider} could not be reached ({ex.HttpRequestError}).", isTransient: true, ex);
        }
        catch (InvalidOperationException ex)
        {
            // The vault's handler refuses before sending: a host the secret does not allow, a secret that is gone or not a DNS token.
            throw new DnsProviderException($"The vault refused to send the {_provider} credential: {Clean(ex.Message)}", isTransient: false, ex);
        }
    }

    private async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } length && length > _options.MaxResponseBytes)
        {
            throw TooLarge();
        }

        Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using MemoryStream buffer = new();
            byte[] chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > _options.MaxResponseBytes)
                {
                    throw TooLarge();
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }

    private DnsProviderException TooLarge() =>
        new($"{_provider} answered with more than {_options.MaxResponseBytes} bytes.", isTransient: false);

    private TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? header = response.Headers.RetryAfter;
        TimeSpan? wait = header?.Delta ?? (header?.Date is { } date ? date - _time.GetUtcNow() : null);
        return wait is { } w ? (w < TimeSpan.Zero ? TimeSpan.Zero : w) : null;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Provider} is rate limiting; waiting {Wait} before asking again.")]
    private partial void LogThrottled(string provider, TimeSpan wait);
}
