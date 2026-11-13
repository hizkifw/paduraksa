using System.Text.Json;

using Paduraksa.Proxy.Net;

namespace Paduraksa.Application.Cloudflare;

/// <summary>
/// Fetches Cloudflare's edge ranges: the JSON endpoint first, then the two plain-text ones when that fails, each response
/// bounded in size and time, each list checked by <see cref="EdgeRangeList.TryCreate"/> before it is believed. A list that
/// fails the checks counts as a failed source, so the text endpoints get their turn after a suspicious JSON answer.
/// </summary>
/// <remarks>The response is data from the network: it is parsed, never executed or followed, redirects aren't taken, and
/// only the checked CIDR blocks leave this class (a reason carries at most a short, cut excerpt).</remarks>
internal sealed class CloudflareRangeFetcher(HttpClient http, CloudflareRangeRefreshOptions options, TimeProvider time)
{
    /// <summary>A list, or why none was accepted (every source's reason).</summary>
    public async Task<(EdgeRangeList? List, string? Failure)> FetchAsync(CancellationToken cancellationToken)
    {
        var api = await TryAsync(FromApiAsync, cancellationToken).ConfigureAwait(false);
        if (api.List is not null)
        {
            return api;
        }

        var text = await TryAsync(FromTextAsync, cancellationToken).ConfigureAwait(false);
        return text.List is not null ? text : (null, $"{api.Failure}; {text.Failure}");
    }

    private static async Task<(EdgeRangeList? List, string? Failure)> TryAsync(
        Func<CancellationToken, Task<EdgeRangeList>> fetch, CancellationToken cancellationToken)
    {
        try
        {
            return (await fetch(cancellationToken).ConfigureAwait(false), null);
        }
        catch (RangeFetchException ex)
        {
            return (null, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // A timeout surfaces as a cancellation of the linked source, not of the caller's token.
            return (null, ex switch
            {
                OperationCanceledException => "timed out",
                HttpRequestException http => $"network error ({http.HttpRequestError})",
                JsonException => "not valid JSON",
                _ => "the response couldn't be read",
            });
        }
    }

    private async Task<EdgeRangeList> FromApiAsync(CancellationToken cancellationToken)
    {
        var body = await GetAsync(options.ApiUrl, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || (root.TryGetProperty("success", out var success) && success.ValueKind != JsonValueKind.True))
        {
            throw new RangeFetchException($"{Where(options.ApiUrl)} didn't report success");
        }

        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
        {
            throw new RangeFetchException($"{Where(options.ApiUrl)} has no result object");
        }

        List<string> blocks = [];
        foreach (var name in (string[])["ipv4_cidrs", "ipv6_cidrs"])
        {
            if (!result.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            {
                throw new RangeFetchException($"{Where(options.ApiUrl)} has no {name} list");
            }

            foreach (var entry in array.EnumerateArray())
            {
                blocks.Add(entry.ValueKind == JsonValueKind.String ? Text(entry) : string.Empty);
            }
        }

        return Checked(blocks, Where(options.ApiUrl));
    }

    // The document is parsed as UTF-8 bytes and a string is only transcoded here, where invalid UTF-8 surfaces as an InvalidOperationException: a bad answer like
    // any other, which must not end the fetch before the text endpoints have had their turn.
    private string Text(JsonElement element)
    {
        try
        {
            return element.GetString()!;
        }
        catch (InvalidOperationException)
        {
            throw new RangeFetchException($"{Where(options.ApiUrl)} has text that is not valid UTF-8");
        }
    }

    private async Task<EdgeRangeList> FromTextAsync(CancellationToken cancellationToken)
    {
        List<string> blocks = [];
        foreach (var url in new[] { options.TextV4Url, options.TextV6Url })
        {
            var body = await GetAsync(url, cancellationToken).ConfigureAwait(false);
            foreach (var line in System.Text.Encoding.UTF8.GetString(body).Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    blocks.Add(trimmed);
                }
            }
        }

        // Both lists are judged together (each family has to be there), so a refusal can't be pinned on one of the two URLs.
        return Checked(blocks, $"{Where(options.TextV4Url)}{options.TextV4Url.AbsolutePath} and {Where(options.TextV6Url)}{options.TextV6Url.AbsolutePath}");
    }

    private EdgeRangeList Checked(List<string> blocks, string source) =>
        EdgeRangeList.TryCreate(blocks, time.GetUtcNow(), out var list, out var reason)
            ? list
            : throw new RangeFetchException($"{source} gave a list that was refused: {reason}");

    private async Task<byte[]> GetAsync(Uri url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new RangeFetchException($"{Where(url)} answered {(int)response.StatusCode}");
        }

        if (response.Content.Headers.ContentLength > options.MaxBodyBytes)
        {
            throw new RangeFetchException($"{Where(url)} is larger than {options.MaxBodyBytes} bytes");
        }

        var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using MemoryStream buffer = new();
            var chunk = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > options.MaxBodyBytes)
                {
                    throw new RangeFetchException($"{Where(url)} is larger than {options.MaxBodyBytes} bytes");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }

    private static string Where(Uri url) => url.Host;

    private sealed class RangeFetchException(string message) : Exception(message);
}
