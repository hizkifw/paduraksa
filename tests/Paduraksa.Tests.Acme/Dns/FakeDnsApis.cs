using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using Microsoft.Extensions.Logging;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Tests.Acme.Dns;

/// <summary>One request a fake DNS API received, with the credential header the fake handler "injected".</summary>
internal sealed record ApiCall(HttpMethod Method, Uri Url, string? Body, string Authorization)
{
    public string Path => Url.AbsolutePath;

    public string Query => Url.Query;

    public NameValueCollectionView Params => new(HttpUtility.ParseQueryString(Url.Query));

    public JsonNode? Json => Body is null ? null : JsonNode.Parse(Body);

    public override string ToString() => $"{Method} {Url.PathAndQuery}";
}

/// <summary>A view of a query string that does not need System.Collections.Specialized in the tests.</summary>
internal readonly struct NameValueCollectionView(System.Collections.Specialized.NameValueCollection values)
{
    public string? this[string name] => values[name];
}

/// <summary>
/// An <see cref="ISecretHttpHandlerFactory"/> that does what the vault's does as far as a provider can tell: https only, only the
/// hosts the injection names, the credential put on the request by the handler (never by the caller), and the same
/// <see cref="InvalidOperationException"/> when it refuses. The "API" behind it is a function.
/// </summary>
internal sealed class FakeSecretHandlers(Func<ApiCall, HttpResponseMessage> api) : ISecretHttpHandlerFactory
{
    public const string Token = "tok_S3CR3T_value_0123456789";

    public List<ApiCall> Calls { get; } = [];

    public List<SecretInjection> Injections { get; } = [];

    /// <summary>Throws this from the transport instead of answering (a network failure).</summary>
    public Exception? TransportFailure { get; set; }

    public HttpMessageHandler CreateAuthenticatingHandler(SecretId id, SecretInjection how, SecretHttpTransportOptions transport)
    {
        Injections.Add(how);
        return new Handler(this, how);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json, params (string Name, string Value)[] headers)
    {
        HttpResponseMessage response = new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        foreach ((string name, string value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    public static HttpResponseMessage Empty(HttpStatusCode status) => new(status);

    private sealed class Handler(FakeSecretHandlers owner, SecretInjection how) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri url = request.RequestUri!;
            if (url.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException("the request is not an absolute https URL");
            }

            if (!how.AllowedHosts.Contains(url.IdnHost) || url.Port != 443)
            {
                throw new InvalidOperationException($"the destination '{url.IdnHost}' is not one the caller allowed");
            }

            string authorization = how.Kind switch
            {
                SecretInjectionKind.Authorization => $"{how.Scheme} {Token}",
                _ => throw new NotSupportedException(),
            };
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (owner.Calls)
            {
                owner.Calls.Add(new ApiCall(request.Method, url, body, authorization));
            }

            if (owner.TransportFailure is { } failure)
            {
                throw failure;
            }

            HttpResponseMessage response = owner._api(new ApiCall(request.Method, url, body, authorization));
            response.RequestMessage = request;
            return response;
        }
    }

    private readonly Func<ApiCall, HttpResponseMessage> _api = api;
}

/// <summary>A logger that keeps what was logged, for "the token is never logged" checks.</summary>
internal sealed class CapturingLogs : ILoggerFactory
{
    private readonly List<string> _entries = [];

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(_entries);

    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();

    public void Dispose()
    {
    }

    private sealed class Logger(List<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (entries)
            {
                entries.Add(formatter(state, exception) + exception);
            }
        }
    }
}

/// <summary>
/// A Cloudflare v4 API in memory: zones, TXT records, the token check, paging and the response quirks the provider has to cope with
/// (TXT content returned in quotes, an identical record refused with 81058).
/// </summary>
internal sealed class FakeCloudflare
{
    private readonly Dictionary<string, string> _zones = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Zone, string Id, string Type, string Name, string Content)> _records = [];
    private int _next;

    public bool QuoteContent { get; set; }

    public int PageSize { get; set; } = 100;

    public string TokenStatus { get; set; } = "active";

    /// <summary>Answers instead of the fake, when it returns something.</summary>
    public Func<ApiCall, HttpResponseMessage?>? Intercept { get; set; }

    public bool UserTokenVerifySupported { get; set; } = true;

    public void AddZone(string name) => _zones[name] = "zone-" + name.Replace('.', '-');

    public IReadOnlyList<(string Name, string Content)> Records(string zoneName) =>
        [.. _records.Where(r => r.Zone == _zones[zoneName] && r.Type == "TXT").Select(r => (r.Name, r.Content))];

    public void Seed(string zoneName, string name, string content) => _records.Add((_zones[zoneName], "rec-" + _next++, "TXT", name, content));

    public void SeedCname(string zoneName, string name, string target) => _records.Add((_zones[zoneName], "rec-" + _next++, "CNAME", name, target));

    public HttpResponseMessage Handle(ApiCall call)
    {
        if (Intercept?.Invoke(call) is { } intercepted)
        {
            return intercepted;
        }

        if (call.Authorization != "Bearer " + FakeSecretHandlers.Token)
        {
            return Error(HttpStatusCode.Unauthorized, 10000, "Authentication error");
        }

        string[] parts = call.Path.Replace("/client/v4/", string.Empty, StringComparison.Ordinal).Split('/');
        switch (parts)
        {
            case ["user", "tokens", "verify"]:
                return UserTokenVerifySupported
                    ? Ok($$$"""{"success":true,"errors":[],"result":{"id":"t","status":"{{{TokenStatus}}}"}}""")
                    : Error(HttpStatusCode.Unauthorized, 1000, "Invalid API Token");
            case ["zones"]:
                if (call.Params["per_page"] is { } perPage && int.Parse(perPage, System.Globalization.CultureInfo.InvariantCulture) is < 5 or > 50)
                {
                    return Error(HttpStatusCode.BadRequest, 1006, "Invalid value for per_page: must be between 5 and 50");
                }

                if (call.Params["name"] is { } zoneName)
                {
                    string result = _zones.TryGetValue(zoneName, out string? zoneId) ? $$"""[{"id":"{{zoneId}}","name":"{{zoneName}}"}]""" : "[]";
                    return Ok($$$"""{"success":true,"errors":[],"result":{{{result}}}}""");
                }

                string all = string.Join(',', _zones.Select(z => $$"""{"id":"{{z.Value}}","name":"{{z.Key}}"}"""));
                return Ok($$"""{"success":true,"errors":[],"result":[{{all}}]}""");
            case ["zones", var zone, "dns_records"] when call.Method == HttpMethod.Get:
                {
                    var matching = _records.Where(r => r.Zone == zone && r.Type == call.Params["type"] && r.Name == call.Params["name"]).ToList();
                    int page = int.Parse(call.Params["page"] ?? "1", System.Globalization.CultureInfo.InvariantCulture);
                    int pages = Math.Max(1, (matching.Count + PageSize - 1) / PageSize);
                    string items = string.Join(',', matching.Skip((page - 1) * PageSize).Take(PageSize).Select(r =>
                        $$"""{"id":"{{r.Id}}","type":"TXT","name":"{{r.Name}}","content":{{JsonValue.Create(QuoteContent ? "\"" + r.Content + "\"" : r.Content)!.ToJsonString()}}}"""));
                    return Ok($$$"""{"success":true,"errors":[],"result":[{{{items}}}],"result_info":{"page":{{{page}}},"per_page":{{{PageSize}}},"total_pages":{{{pages}}},"count":1,"total_count":{{{matching.Count}}}}}""");
                }

            case ["zones", var zone, "dns_records"] when call.Method == HttpMethod.Post:
                {
                    JsonNode body = call.Json!;
                    string name = (string)body["name"]!;
                    string content = (string)body["content"]!;
                    if (_records.Any(r => r.Zone == zone && r.Type == "TXT" && r.Name == name && r.Content == content))
                    {
                        return Error(HttpStatusCode.BadRequest, 81058, "An identical record already exists.");
                    }

                    string id = "rec-" + _next++;
                    _records.Add((zone, id, "TXT", name, content));
                    return Ok($$$"""{"success":true,"errors":[],"result":{"id":"{{{id}}}"}}""");
                }

            case ["zones", var zone, "dns_records", var id] when call.Method == HttpMethod.Delete:
                return _records.RemoveAll(r => r.Zone == zone && r.Id == id) > 0
                    ? Ok($$$"""{"success":true,"errors":[],"result":{"id":"{{{id}}}"}}""")
                    : Error(HttpStatusCode.NotFound, 81044, "Record does not exist.");
            default:
                return Error(HttpStatusCode.NotFound, 7003, "No route");
        }
    }

    public static HttpResponseMessage Ok(string json) => FakeSecretHandlers.Json(HttpStatusCode.OK, json);

    public static HttpResponseMessage Error(HttpStatusCode status, int code, string message, params (string, string)[] headers) =>
        FakeSecretHandlers.Json(status, $$"""{"success":false,"errors":[{"code":{{code}},"message":"{{message}}"}],"messages":[],"result":null}""", headers);
}

/// <summary>
/// A deSEC API in memory: domains, whole RRsets, minimum TTL, bulk writes and the token check. A write is validated as deSEC
/// does: a PUT needs the whole resource (<c>subname</c>, <c>type</c>, <c>ttl</c>, <c>records</c>), bulk items need <c>subname</c> and
/// <c>type</c>, an empty <c>records</c> deletes, and a TTL below the domain's minimum is refused.
/// </summary>
internal sealed class FakeDesec
{
    private readonly Dictionary<string, int> _domains = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Domain, string Subname, string Type), (int Ttl, List<string> Records)> _rrsets = [];

    public Func<ApiCall, HttpResponseMessage?>? Intercept { get; set; }

    /// <summary>Number of requests that wrote (anything but GET), as deSEC's per-domain limit counts them.</summary>
    public int Writes { get; private set; }

    public void AddDomain(string name, int minimumTtl = 3600) => _domains[name] = minimumTtl;

    public void Seed(string domain, string subname, int ttl, params string[] records) => _rrsets[(domain, subname, "TXT")] = (ttl, [.. records]);

    public void SeedCname(string domain, string subname, string target) => _rrsets[(domain, subname, "CNAME")] = (3600, [target]);

    public (int Ttl, IReadOnlyList<string> Records)? Rrset(string domain, string subname) =>
        _rrsets.TryGetValue((domain, subname, "TXT"), out var set) ? (set.Ttl, set.Records) : null;

    public HttpResponseMessage Handle(ApiCall call)
    {
        if (Intercept?.Invoke(call) is { } intercepted)
        {
            return intercepted;
        }

        if (call.Authorization != "Token " + FakeSecretHandlers.Token)
        {
            return Detail(HttpStatusCode.Unauthorized, "Invalid token.");
        }

        if (call.Method != HttpMethod.Get)
        {
            Writes++;
        }

        string[] parts = call.Path.Replace("/api/v1/", string.Empty, StringComparison.Ordinal).Trim('/').Split('/');
        switch (parts)
        {
            case ["domains"]:
                {
                    IEnumerable<KeyValuePair<string, int>> found = call.Params["owns_qname"] is { } qname
                        ? _domains.Where(d => qname == d.Key || qname.EndsWith("." + d.Key, StringComparison.OrdinalIgnoreCase)).OrderByDescending(d => d.Key.Length).Take(1)
                        : _domains;
                    return FakeSecretHandlers.Json(HttpStatusCode.OK, "[" + string.Join(',', found.Select(d => $$"""{"name":"{{d.Key}}","minimum_ttl":{{d.Value}}}""")) + "]");
                }

            case ["domains", var domain, "rrsets"] when call.Method == HttpMethod.Post || call.Method == HttpMethod.Patch:
                {
                    // Bulk: an array of whole items. POST creates (an existing set is a 400); PATCH changes (a missing set is a 400).
                    if (call.Json is not JsonArray items)
                    {
                        return FakeSecretHandlers.Json(HttpStatusCode.BadRequest, """{"non_field_errors":["Expected a list of items."]}""");
                    }

                    bool create = call.Method == HttpMethod.Post;
                    Dictionary<(string, string, string), (int, List<string>)?> staged = [];
                    foreach (JsonNode? item in items)
                    {
                        if (item?["subname"] is null || item["type"] is null)
                        {
                            return FakeSecretHandlers.Json(HttpStatusCode.BadRequest, """[{"type":["This field is required."]}]""");
                        }

                        (string, string, string) key = (domain, (string)item["subname"]!, (string)item["type"]!);
                        bool exists = _rrsets.ContainsKey(key);
                        if (create == exists)
                        {
                            return FakeSecretHandlers.Json(HttpStatusCode.BadRequest, create
                                ? """[{"non_field_errors":["Another RRset with the same subdomain and type exists for this domain."]}]"""
                                : """[{"detail":"Not found."}]""");
                        }

                        List<string> records = [.. item["records"]!.AsArray().Select(n => (string)n!)];
                        if (records.Count == 0)
                        {
                            staged[key] = null;
                            continue;
                        }

                        int ttl = item["ttl"] is { } t ? (int)t : _rrsets[key].Ttl;
                        if (ttl < _domains[domain])
                        {
                            return FakeSecretHandlers.Json(HttpStatusCode.BadRequest, $$"""[{"ttl":["Ensure this value is greater than or equal to {{_domains[domain]}}."]}]""");
                        }

                        staged[key] = (ttl, records);
                    }

                    foreach (var (key, value) in staged)
                    {
                        if (value is { } set)
                        {
                            _rrsets[key] = set;
                        }
                        else
                        {
                            _rrsets.Remove(key);
                        }
                    }

                    return FakeSecretHandlers.Json(create ? HttpStatusCode.Created : HttpStatusCode.OK, "[]");
                }

            case ["domains", var domain, "rrsets", var sub, var type]:
                {
                    string subname = sub == "@" ? string.Empty : sub;
                    if (!_rrsets.TryGetValue((domain, subname, type), out var set))
                    {
                        return Detail(HttpStatusCode.NotFound, "Not found.");
                    }

                    if (call.Method == HttpMethod.Get)
                    {
                        return FakeSecretHandlers.Json(HttpStatusCode.OK, $$"""{"subname":"{{subname}}","type":"{{type}}","ttl":{{set.Ttl}},"records":[{{string.Join(',', set.Records.Select(r => JsonValue.Create(r)!.ToJsonString()))}}]}""");
                    }

                    if (call.Method == HttpMethod.Delete)
                    {
                        _rrsets.Remove((domain, subname, type));
                        return FakeSecretHandlers.Empty(HttpStatusCode.NoContent);
                    }

                    JsonNode body = call.Json!;
                    if (call.Method == HttpMethod.Put && (body["subname"] is null || body["type"] is null || body["ttl"] is null || body["records"] is null))
                    {
                        return FakeSecretHandlers.Json(HttpStatusCode.BadRequest, """{"subname":["This field is required."],"type":["This field is required."]}""");
                    }

                    int ttl = body["ttl"] is { } given ? (int)given : set.Ttl;
                    if (ttl < _domains[domain])
                    {
                        return FakeSecretHandlers.Json(HttpStatusCode.BadRequest, $$"""{"ttl":["Ensure this value is greater than or equal to {{_domains[domain]}}."]}""");
                    }

                    _rrsets[(domain, subname, type)] = (ttl, body["records"] is { } records ? [.. records.AsArray().Select(n => (string)n!)] : set.Records);
                    return FakeSecretHandlers.Json(HttpStatusCode.OK, "{}");
                }

            default:
                return Detail(HttpStatusCode.NotFound, "No route");
        }
    }

    public static HttpResponseMessage Detail(HttpStatusCode status, string detail, params (string, string)[] headers) =>
        FakeSecretHandlers.Json(status, $$"""{"detail":"{{detail}}"}""", headers);
}
