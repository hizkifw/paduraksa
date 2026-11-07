using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Acme.Dns;

/// <summary>
/// <see cref="IDnsProvider"/> for deSEC (<c>desec.io</c>) with an API token. The token is a vault secret: every call goes
/// through the vault's authenticating handler, which adds <c>Authorization: Token</c> inside the vault and talks only to
/// <see cref="ApiHost"/> over https (PLAN §8.2, §9).
/// </summary>
/// <remarks>
/// <para>
/// <b>Domain.</b> <c>GET /domains/?owns_qname=</c> names the account's most specific domain that contains the record name;
/// none is "no zone" (permanent). The <c>subname</c> is the name below that domain.
/// </para>
/// <para>
/// <b>RRsets are whole.</b> deSEC has no per-record API: the TXT RRset at a name is read (404 means there is none), the values
/// are added or removed, and the full <c>records</c> list is written back, which keeps the wildcard's and the base name's values
/// side by side. There is no ETag, so two writers of the same set at the same moment could lose one value; the orchestrator
/// publishes an order's values in one batch. Values are stored quoted (<c>"value"</c>) and compared unquoted.
/// </para>
/// <para>
/// <b>Writes are batched.</b> deSEC limits writes per domain (2 a second, 15 a minute, 100 an hour, 300 a day), and a publish of
/// many names would meet it. A batch (and a single call is a batch of one) is grouped by domain, and each domain gets at most
/// two writes: one bulk <c>POST /domains/{d}/rrsets/</c> for the sets that do not exist and one bulk <c>PATCH</c> for those that
/// do (<c>records: []</c> deletes a set). A <c>POST</c> that meets a set created in the meantime (400) re-reads and goes on
/// as a <c>PATCH</c> once.
/// </para>
/// <para>
/// <b>TTL.</b> deSEC refuses a TTL below the domain's <c>minimum_ttl</c> (3600 by default), so the TTL is at least that, and
/// never lowers one the set already has. <b>Rate limits:</b> a 429 whose <c>Retry-After</c> is within
/// <see cref="DnsProviderOptions.MaxInlineRetryAfter"/> is waited out once; a longer one is a rate-limited
/// <see cref="DnsProviderException"/> carrying the hint. <b>CNAME:</b> a name that is a CNAME is refused (permanent), because the
/// CA would follow it to a zone this provider is not asked about.
/// </para>
/// </remarks>
internal sealed partial class DesecDnsProvider : IDnsProvider
{
    /// <summary>The only host the credential is sent to.</summary>
    public const string ApiHost = "desec.io";

    /// <summary>The TTL deSEC's default account is held to.</summary>
    private const int DefaultMinimumTtl = 3600;

    private static readonly Uri Api = new($"https://{ApiHost}/api/v1/");

    private readonly ISecretHttpHandlerFactory _handlers;
    private readonly SecretId _credential;
    private readonly DnsProviderOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public DesecDnsProvider(ISecretHttpHandlerFactory handlers, SecretId credential, DnsProviderOptions options, TimeProvider time, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _handlers = handlers;
        _credential = credential;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public ValueTask CreateTxtAsync(string recordName, string value, CancellationToken cancellationToken) =>
        ApplyAsync([new Change(DnsNames.Normalize(recordName), DnsNames.RequireTxtValue(value), Add: true)], cancellationToken);

    public ValueTask DeleteTxtAsync(string recordName, string value, CancellationToken cancellationToken) =>
        ApplyAsync([new Change(DnsNames.Normalize(recordName), DnsNames.RequireTxtValue(value), Add: false)], cancellationToken);

    public ValueTask CreateTxtBatchAsync(IReadOnlyList<DnsChallengeRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        return ApplyAsync([.. records.Select(r => new Change(DnsNames.Normalize(r.Name), DnsNames.RequireTxtValue(r.Value), Add: true))], cancellationToken);
    }

    public ValueTask DeleteTxtBatchAsync(IReadOnlyList<DnsChallengeRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        return ApplyAsync([.. records.Select(r => new Change(DnsNames.Normalize(r.Name), DnsNames.RequireTxtValue(r.Value), Add: false))], cancellationToken);
    }

    public async ValueTask<DnsCredentialCheck> ValidateCredentialsAsync(CancellationToken cancellationToken)
    {
        // One bounded read that needs the token to be good and to read domains; listing them all would page above 500.
        using DnsApiClient api = Open();
        DnsApiResponse response = await api.SendAsync(HttpMethod.Get, Url("domains/?owns_qname=paduraksa-credential-check.invalid"), null, cancellationToken).ConfigureAwait(false);
        return response.IsSuccess
            ? new DnsCredentialCheck(true)
            : new DnsCredentialCheck(false, $"deSEC did not accept the token (HTTP {(int)response.Status}{(ErrorText(response) is { Length: > 0 } text ? ": " + text : string.Empty)}).");
    }

    private static Uri Url(string relative) => new(Api, relative);

    private static Uri RrsetUrl(string domain, string subname, string type) =>
        Url($"domains/{Uri.EscapeDataString(domain)}/rrsets/{(subname.Length == 0 ? "@" : Uri.EscapeDataString(subname))}/{type}/");

    private static Uri RrsetsUrl(string domain) => Url($"domains/{Uri.EscapeDataString(domain)}/rrsets/");

    /// <summary>deSEC's error text: <c>detail</c>, or the first field's first message (<c>ttl: Ensure this value ...</c>). Empty when there is none.</summary>
    private static string ErrorText(DnsApiResponse response)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(response.Body);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                root = root[0];
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            if (root.TryGetProperty("detail", out JsonElement detail) && detail.ValueKind == JsonValueKind.String)
            {
                return DnsApiClient.Clean(detail.GetString());
            }

            foreach (JsonProperty field in root.EnumerateObject())
            {
                JsonElement message = field.Value.ValueKind == JsonValueKind.Array && field.Value.GetArrayLength() > 0 ? field.Value[0] : field.Value;
                if (message.ValueKind == JsonValueKind.String)
                {
                    return DnsApiClient.Clean($"{field.Name}: {message.GetString()}");
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON (a proxy's page): no text is better than that page.
        }

        return string.Empty;
    }

    /// <summary>The records after the changes: values added if missing, removed if present, compared unquoted.</summary>
    private static List<string> Merge(IEnumerable<string>? existing, IEnumerable<Change> changes)
    {
        List<string> records = [.. existing ?? []];
        foreach (Change change in changes)
        {
            int index = records.FindIndex(r => DnsNames.UnquoteTxt(r) == change.Value);
            if (change.Add && index < 0)
            {
                records.Add(DnsNames.QuoteTxt(change.Value));
            }
            else if (!change.Add && index >= 0)
            {
                records.RemoveAt(index);
            }
        }

        return records;
    }

    private DnsApiClient Open() =>
        new(
            _handlers.CreateAuthenticatingHandler(_credential, SecretInjection.Authorization("Token", [ApiHost]), _options.Transport),
            "deSEC",
            _options,
            _time,
            _logger);

    private async ValueTask ApplyAsync(List<Change> changes, CancellationToken cancellationToken)
    {
        if (changes.Count == 0)
        {
            return;
        }

        using DnsApiClient api = Open();

        // Which RRset each change lands in (one lookup per name).
        Dictionary<string, (string Domain, string Subname, int MinimumTtl)> located = [];
        foreach (string name in changes.Select(c => c.Name).Distinct(StringComparer.Ordinal))
        {
            located[name] = await FindDomainAsync(api, name, cancellationToken).ConfigureAwait(false);
        }

        List<Target> targets = [];
        foreach (IGrouping<(string Domain, string Subname), Change> group in changes.GroupBy(c => (located[c.Name].Domain, located[c.Name].Subname)))
        {
            int minimumTtl = located[group.First().Name].MinimumTtl;
            if (group.Any(c => c.Add) && await ExistsAsync(api, group.Key.Domain, group.Key.Subname, "CNAME", cancellationToken).ConfigureAwait(false))
            {
                throw new DnsProviderException(
                    $"{group.First().Name} is a CNAME in deSEC; delegating the challenge to another zone is not supported yet.",
                    isTransient: false);
            }

            DesecRrset? existing = await GetRrsetAsync(api, group.Key.Domain, group.Key.Subname, cancellationToken).ConfigureAwait(false);
            targets.Add(new Target(group.Key.Domain, group.Key.Subname, minimumTtl, [.. group], existing));
        }

        foreach (IGrouping<string, Target> domain in targets.GroupBy(t => t.Domain, StringComparer.Ordinal))
        {
            List<DesecRrsetWrite> creates = [];
            List<DesecRrsetWrite> updates = [];
            foreach (Target target in domain)
            {
                List<string> records = Merge(target.Existing?.Records, target.Changes);
                if (target.Existing is null)
                {
                    if (records.Count > 0)
                    {
                        creates.Add(Write(target, records));
                    }
                }
                else if (!records.SequenceEqual(target.Existing.Records ?? []))
                {
                    updates.Add(Write(target, records));
                }
            }

            if (creates.Count > 0)
            {
                DnsApiResponse response = await api.SendAsync(
                    HttpMethod.Post, RrsetsUrl(domain.Key), DnsApiClient.Json(creates, DesecJsonContext.Default.ListDesecRrsetWrite), cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccess)
                {
                    if (response.Status != HttpStatusCode.BadRequest)
                    {
                        throw api.Failure("create the TXT RRsets", response, ErrorText(response));
                    }

                    // Another writer may have created a set since it was read: merge into what is there now, once.
                    DnsProviderException refusal = api.Failure("create the TXT RRsets", response, ErrorText(response));
                    foreach (Target target in domain.Where(t => t.Existing is null))
                    {
                        DesecRrset now = await GetRrsetAsync(api, target.Domain, target.Subname, cancellationToken).ConfigureAwait(false) ?? throw refusal;
                        updates.Add(Write(target with { Existing = now }, Merge(now.Records, target.Changes)));
                    }
                }
            }

            if (updates.Count > 0)
            {
                DnsApiResponse response = await api.SendAsync(
                    HttpMethod.Patch, RrsetsUrl(domain.Key), DnsApiClient.Json(updates, DesecJsonContext.Default.ListDesecRrsetWrite), cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccess)
                {
                    throw api.Failure("write the TXT RRsets", response, ErrorText(response));
                }
            }

            LogWritten(domain.Key, creates.Count, updates.Count);
        }
    }

    private DesecRrsetWrite Write(Target target, List<string> records) =>
        new()
        {
            Subname = target.Subname,
            Type = "TXT",
            Ttl = Math.Max(Math.Max(_options.RecordTtlSeconds, target.MinimumTtl), target.Existing?.Ttl ?? 0),
            Records = records,
        };

    private static async Task<(string Domain, string Subname, int MinimumTtl)> FindDomainAsync(DnsApiClient api, string name, CancellationToken cancellationToken)
    {
        DnsApiResponse response = await api.SendAsync(HttpMethod.Get, Url($"domains/?owns_qname={Uri.EscapeDataString(name)}"), null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            throw api.Failure("look up the domain", response, ErrorText(response));
        }

        List<DesecDomain> domains = response.Read(DesecJsonContext.Default.ListDesecDomain, "deSEC");
        DesecDomain? owner = domains
            .Where(d => d.Name is { Length: > 0 } && (string.Equals(name, d.Name, StringComparison.OrdinalIgnoreCase) || name.EndsWith("." + d.Name, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(d => d.Name!.Length)
            .FirstOrDefault();
        if (owner is null)
        {
            throw new DnsProviderException($"deSEC has no domain for {name} in this account.", isTransient: false);
        }

        string domain = owner.Name!.ToLowerInvariant();
        string subname = name.Length == domain.Length ? string.Empty : name[..^(domain.Length + 1)];
        return (domain, subname, owner.MinimumTtl ?? DefaultMinimumTtl);
    }

    /// <summary>The TXT RRset at the subname, or null when there is none (404).</summary>
    private static async Task<DesecRrset?> GetRrsetAsync(DnsApiClient api, string domain, string subname, CancellationToken cancellationToken)
    {
        DnsApiResponse response = await api.SendAsync(HttpMethod.Get, RrsetUrl(domain, subname, "TXT"), null, cancellationToken).ConfigureAwait(false);
        if (response.Status == HttpStatusCode.NotFound)
        {
            return null;
        }

        return response.IsSuccess
            ? response.Read(DesecJsonContext.Default.DesecRrset, "deSEC")
            : throw api.Failure("read the TXT RRset", response, ErrorText(response));
    }

    private static async Task<bool> ExistsAsync(DnsApiClient api, string domain, string subname, string type, CancellationToken cancellationToken)
    {
        DnsApiResponse response = await api.SendAsync(HttpMethod.Get, RrsetUrl(domain, subname, type), null, cancellationToken).ConfigureAwait(false);
        return response.IsSuccess
            ? true
            : response.Status == HttpStatusCode.NotFound
                ? false
                : throw api.Failure($"read the {type} RRset", response, ErrorText(response));
    }

    private sealed record Change(string Name, string Value, bool Add);

    private sealed record Target(string Domain, string Subname, int MinimumTtl, List<Change> Changes, DesecRrset? Existing);

    [LoggerMessage(Level = LogLevel.Information, Message = "deSEC: domain {Domain} written ({Created} RRset(s) created, {Updated} updated or deleted).")]
    private partial void LogWritten(string domain, int created, int updated);
}
