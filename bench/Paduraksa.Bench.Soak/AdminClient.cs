using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Paduraksa.Bench.Soak;

/// <summary>One admin API answer: the status, the body, and how long the call took.</summary>
internal sealed record AdminResult(HttpStatusCode Status, string Body, string? ETag, double Milliseconds, string[] SetCookies)
{
    public string? SessionCookie => SetCookies.Select(c => c.Split(';')[0]).FirstOrDefault(c => c.StartsWith("__Host-paduraksa-session=", StringComparison.Ordinal));

    public bool Ok => (int)Status is >= 200 and < 300;

    public JsonNode? Json => Body.Length == 0 ? null : JsonNode.Parse(Body);
}

/// <summary>The admin API as the soak uses it: a bearer token, the admin listener's self-signed certificate not checked (loopback, the local CA's), and timings.</summary>
internal sealed class AdminClient : IDisposable
{
    private readonly HttpClient _http;

    public AdminClient(int port, string? token = null)
    {
        HttpClientHandler handler = new() { ServerCertificateCustomValidationCallback = static (_, _, _, _) => true, UseCookies = false };
        _http = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(60) };
        if (token is not null)
        {
            Token = token;
        }
    }

    public string? Token
    {
        get;
        set
        {
            field = value;
            _http.DefaultRequestHeaders.Authorization = value is null ? null : new AuthenticationHeaderValue("Bearer", value);
        }
    }

    public Task<AdminResult> GetAsync(string path, CancellationToken ct = default) => SendAsync(HttpMethod.Get, path, null, null, ct);

    public Task<AdminResult> PostAsync(string path, string body, string contentType = "application/json", CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, path, body, contentType, ct);

    public Task<AdminResult> PutAsync(string path, string body, long version, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, path, body, "application/json", ct, "\"" + version + "\"");

    public async Task<AdminResult> SendAsync(HttpMethod method, string path, string? body, string? contentType, CancellationToken ct, string? ifMatch = null, string? cookie = null)
    {
        using HttpRequestMessage request = new(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, contentType ?? "application/json");
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            request.Headers.TryAddWithoutValidation("Origin", _http.BaseAddress!.GetLeftPart(UriPartial.Authority));
        }

        var watch = Stopwatch.StartNew();
        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        watch.Stop();
        var etag = response.Headers.ETag?.Tag.Trim('"');
        return new AdminResult(
            response.StatusCode, text, etag, watch.Elapsed.TotalMilliseconds,
            response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies.ToArray() : []);
    }

    public void Dispose() => _http.Dispose();
}
