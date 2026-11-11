using System.Text.Json.Serialization;

using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Config;

/// <summary>
/// The pages a host answers with in place of the built-in ones, one entry per status the gate and the upstream can answer
/// with a page, and whether an upstream's own error bodies are replaced by them. Each entry inherits on its own and is
/// replaced whole by a layer that sets it (a heading from one layer and a document from another make no page). A status with
/// no entry in any layer is served from the built-in page.
/// </summary>
public sealed record ErrorPagesSettings : ISettingsLayer
{
    /// <summary>403: an access list, rule set or Cloudflare check refused the request.</summary>
    [JsonPropertyName("403")]
    public ErrorPageContent? Forbidden { get; init; }

    /// <summary>404: no route takes the path, or the host is unknown.</summary>
    [JsonPropertyName("404")]
    public ErrorPageContent? NotFound { get; init; }

    /// <summary>413: the request body is over the host's or route's limit.</summary>
    [JsonPropertyName("413")]
    public ErrorPageContent? ContentTooLarge { get; init; }

    /// <summary>414: the request target is over the host's limit.</summary>
    [JsonPropertyName("414")]
    public ErrorPageContent? UriTooLong { get; init; }

    /// <summary>421: the connection can't be used for the host (a client that coalesced it must retry on its own).</summary>
    [JsonPropertyName("421")]
    public ErrorPageContent? MisdirectedRequest { get; init; }

    /// <summary>429: a rate limit, or a queue of password checks, turned the request away.</summary>
    [JsonPropertyName("429")]
    public ErrorPageContent? TooManyRequests { get; init; }

    /// <summary>431: the request has more headers, or bigger ones, than the host takes.</summary>
    [JsonPropertyName("431")]
    public ErrorPageContent? HeaderFieldsTooLarge { get; init; }

    /// <summary>502: the upstream, or a sign-in service, didn't answer properly.</summary>
    [JsonPropertyName("502")]
    public ErrorPageContent? BadGateway { get; init; }

    /// <summary>503: no healthy destination, or a sign-in service is busy.</summary>
    [JsonPropertyName("503")]
    public ErrorPageContent? ServiceUnavailable { get; init; }

    /// <summary>504: the upstream, or a sign-in service, took too long.</summary>
    [JsonPropertyName("504")]
    public ErrorPageContent? GatewayTimeout { get; init; }

    /// <summary>The page a host in maintenance answers with (<see cref="MaintenanceSettings"/>).</summary>
    [JsonPropertyName("maintenance")]
    public ErrorPageContent? Maintenance { get; init; }

    /// <summary>
    /// Answer with the page above when the upstream's own response is an error with a page here (the statuses of this
    /// record), decided from the status and content type when the response starts: the upstream's body is discarded unread. Never for
    /// an upgrade, an event stream or gRPC. Built-in default: <see cref="UpstreamErrorReplacement.Off"/>.
    /// </summary>
    public UpstreamErrorReplacement? ReplaceUpstream { get; init; }

    [JsonIgnore]
    public bool SetsNothing => ReplaceUpstream is null && Entries().All(static entry => entry.Content is null);

    /// <summary>Every entry with its key, set or not, in <see cref="ErrorPageStatuses.Keys"/> order.</summary>
    public IEnumerable<(int Key, ErrorPageContent? Content)> Entries()
    {
        yield return (403, Forbidden);
        yield return (404, NotFound);
        yield return (413, ContentTooLarge);
        yield return (414, UriTooLong);
        yield return (421, MisdirectedRequest);
        yield return (429, TooManyRequests);
        yield return (431, HeaderFieldsTooLarge);
        yield return (502, BadGateway);
        yield return (503, ServiceUnavailable);
        yield return (504, GatewayTimeout);
        yield return (ErrorPageStatuses.MaintenanceKey, Maintenance);
    }

    /// <summary>This record with the entry for <paramref name="key"/> set to <paramref name="content"/> (null: unset).</summary>
    public ErrorPagesSettings WithEntry(int key, ErrorPageContent? content) => key switch
    {
        403 => this with { Forbidden = content },
        404 => this with { NotFound = content },
        413 => this with { ContentTooLarge = content },
        414 => this with { UriTooLong = content },
        421 => this with { MisdirectedRequest = content },
        429 => this with { TooManyRequests = content },
        431 => this with { HeaderFieldsTooLarge = content },
        502 => this with { BadGateway = content },
        503 => this with { ServiceUnavailable = content },
        504 => this with { GatewayTimeout = content },
        ErrorPageStatuses.MaintenanceKey => this with { Maintenance = content },
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Not a status with an error page."),
    };

    /// <summary>The entry for <paramref name="key"/>, or null when this layer sets none.</summary>
    public ErrorPageContent? Entry(int key) => key switch
    {
        403 => Forbidden,
        404 => NotFound,
        413 => ContentTooLarge,
        414 => UriTooLong,
        421 => MisdirectedRequest,
        429 => TooManyRequests,
        431 => HeaderFieldsTooLarge,
        502 => BadGateway,
        503 => ServiceUnavailable,
        504 => GatewayTimeout,
        ErrorPageStatuses.MaintenanceKey => Maintenance,
        _ => null,
    };
}

/// <summary>
/// One error page: a heading and a sentence rendered into Paduraksa's own layout, or the operator's whole document. The
/// text may use the variables <c>{status}</c>, <c>{statusText}</c>, <c>{requestId}</c>, <c>{host}</c> and <c>{cfRay}</c>
/// (<see cref="ErrorPageText"/>); <c>{{</c> writes a literal brace.
/// </summary>
public sealed record ErrorPageContent
{
    /// <summary>How the page is written. Default: <see cref="ErrorPageMode.Message"/>.</summary>
    public ErrorPageMode Mode { get; init; }

    /// <summary><see cref="ErrorPageMode.Message"/> only: the heading, 1 to 120 characters on one line. It is also the page's title.</summary>
    public string? Heading { get; init; }

    /// <summary><see cref="ErrorPageMode.Message"/> only: the sentence under the heading, up to 500 characters on one line; may be empty.</summary>
    public string? Text { get; init; }

    /// <summary>
    /// <see cref="ErrorPageMode.Html"/> only: a complete document, up to 64 KiB of UTF-8. It is served with
    /// <c>Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:; base-uri 'none';
    /// form-action 'none'; frame-ancestors 'none'</c>, so inline style and <c>data:</c> images work and nothing runs or loads from elsewhere.
    /// </summary>
    public string? Html { get; init; }
}

/// <summary>
/// A host that is down on purpose. While <see cref="Enabled"/>, every request but the bypassed ones is answered with the
/// maintenance page and <see cref="Status"/> instead of reaching the upstream: after the host's access list, rules and HTTPS
/// redirect, so a refused client is still refused and plain HTTP still redirects, and after the managed <c>robots.txt</c>.
/// Each field inherits on its own.
/// </summary>
public sealed record MaintenanceSettings : ISettingsLayer
{
    /// <summary>Serve the maintenance page. Built-in default: off. Not applied to an admin host (the config warns), so a layer
    /// that turns it on for every host can't lock the dashboard out.</summary>
    public bool? Enabled { get; init; }

    /// <summary>The status of the answer: 503, 200, 500, 502, 504, 403, 404 or 410. Built-in default: 503.</summary>
    public int? Status { get; init; }

    /// <summary>Seconds a client is told to wait (<c>Retry-After</c>), 0 to 604800 (a week); 0 sends none. Built-in default: 0.</summary>
    public int? RetryAfterSeconds { get; init; }

    /// <summary>
    /// A named access list whose allowed addresses and countries reach the host as usual: a request the list's allow rules admit
    /// skips the maintenance page. Only the address rules count (a password is never asked for here, and a list with no allow rule
    /// bypasses nobody). Built-in default: none.
    /// </summary>
    public AccessListId? BypassAccessListId { get; init; }

    [JsonIgnore]
    public bool SetsNothing => Enabled is null && Status is null && RetryAfterSeconds is null && BypassAccessListId is null;
}
