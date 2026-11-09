using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Acme.Dns;

/// <summary>
/// <see cref="IDnsProvider"/> for Cloudflare's v4 API with a scoped API token (Zone:Read and DNS:Edit on the zones to issue
/// for). The token is a vault secret: every call goes through the vault's authenticating handler, which adds
/// <c>Authorization: Bearer</c> inside the vault and talks only to <see cref="ApiHost"/> over https.
/// </summary>
/// <remarks>
/// <b>Zone.</b> The zone of a record name is found by asking <c>GET /zones?name=</c> for the name and each parent, longest
/// first, down to two labels; a token scoped to some zones simply sees no other, so a name outside them is "no zone"
/// (permanent). Found zones are remembered for the life of the provider.
///
/// <b>Records.</b> A wildcard and its base name share <c>_acme-challenge.example.com</c>, so both TXT records exist at once.
/// Create adds nothing if the value is there, and treats Cloudflare's "identical record already exists" (81058) as done, for
/// a racing repeat; delete removes only those whose content equals the value, with or without the quotes Cloudflare may add.
///
/// <b>Errors.</b> 429 and 5xx, timeouts and network failures are transient (a 429 carries Cloudflare's <c>Retry-After</c>);
/// everything else Cloudflare refuses is permanent.
/// </remarks>
internal sealed partial class CloudflareDnsProvider : IDnsProvider
{
    /// <summary>The only host the credential is sent to.</summary>
    public const string ApiHost = "api.cloudflare.com";

    private const int PageSize = 100;
    private const int MaxPages = 20;
    private const int IdenticalRecordExists = 81058;

    private static readonly Uri Api = new($"https://{ApiHost}/client/v4/");

    private readonly ISecretHttpHandlerFactory _handlers;
    private readonly SecretId _credential;
    private readonly DnsProviderOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, string> _zones = new(StringComparer.Ordinal);

    public CloudflareDnsProvider(ISecretHttpHandlerFactory handlers, SecretId credential, DnsProviderOptions options, TimeProvider time, ILogger logger)
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

    public async ValueTask CreateTxtAsync(string recordName, string value, CancellationToken cancellationToken)
    {
        string name = DnsNames.Normalize(recordName);
        DnsNames.RequireTxtValue(value);
        using DnsApiClient api = Open();
        string zone = await FindZoneAsync(api, name, cancellationToken).ConfigureAwait(false);
        if ((await ListAsync(api, zone, "CNAME", name, cancellationToken).ConfigureAwait(false)).FirstOrDefault() is { } alias)
        {
            // Cloudflare would refuse a TXT beside a CNAME, and the CA follows the CNAME: the challenge belongs in the target's zone.
            throw new DnsProviderException(
                $"{name} is a CNAME to {DnsApiClient.Clean(alias.Content)}; delegating the challenge to another zone is not supported yet.",
                isTransient: false);
        }

        if ((await ListAsync(api, zone, "TXT", name, cancellationToken).ConfigureAwait(false)).Any(r => Holds(r, value)))
        {
            return;
        }

        byte[] body = DnsApiClient.Json(
            new CloudflareRecordWrite { Type = "TXT", Name = name, Content = value, Ttl = Math.Max(60, _options.RecordTtlSeconds) },
            CloudflareJsonContext.Default.CloudflareRecordWrite);
        DnsApiResponse response = await api.SendAsync(HttpMethod.Post, Url($"zones/{zone}/dns_records"), body, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccess)
        {
            LogCreated(name);
            return;
        }

        CloudflareEnvelope<object>? error = TryEnvelope(response);
        if (error?.HasError(IdenticalRecordExists) == true)
        {
            return;
        }

        throw api.Failure("create the TXT record", response, error?.FirstError);
    }

    public async ValueTask DeleteTxtAsync(string recordName, string value, CancellationToken cancellationToken)
    {
        string name = DnsNames.Normalize(recordName);
        DnsNames.RequireTxtValue(value);
        using DnsApiClient api = Open();
        string zone = await FindZoneAsync(api, name, cancellationToken).ConfigureAwait(false);
        List<CloudflareRecord> matching = [.. (await ListAsync(api, zone, "TXT", name, cancellationToken).ConfigureAwait(false)).Where(r => Holds(r, value))];
        foreach (CloudflareRecord record in matching)
        {
            DnsApiResponse response = await api.SendAsync(HttpMethod.Delete, Url($"zones/{zone}/dns_records/{Uri.EscapeDataString(record.Id!)}"), null, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccess)
            {
                LogDeleted(name);
            }
            else if (response.Status == HttpStatusCode.NotFound)
            {
                LogAlreadyGone(name);
            }
            else
            {
                throw api.Failure("delete the TXT record", response, TryEnvelope(response)?.FirstError);
            }
        }
    }

    public async ValueTask<DnsCredentialCheck> ValidateCredentialsAsync(CancellationToken cancellationToken)
    {
        using DnsApiClient api = Open();

        // A user token verifies here; an account-owned token answers an error, and the zone read below decides.
        string? verifyProblem = null;
        DnsApiResponse verify = await api.SendAsync(HttpMethod.Get, Url("user/tokens/verify"), null, cancellationToken).ConfigureAwait(false);
        if (verify.IsSuccess && verify.Read(CloudflareJsonContext.Default.CloudflareEnvelopeCloudflareTokenStatus, "Cloudflare") is { Success: true } verified)
        {
            if (!string.Equals(verified.Result?.Status, "active", StringComparison.OrdinalIgnoreCase))
            {
                return new DnsCredentialCheck(false, $"Cloudflare says the token is {DnsApiClient.Clean(verified.Result?.Status ?? "not active")}.");
            }
        }
        else
        {
            verifyProblem = TryEnvelope(verify)?.FirstError ?? $"HTTP {(int)verify.Status}";
        }

        DnsApiResponse zones = await api.SendAsync(HttpMethod.Get, Url("zones?per_page=5"), null, cancellationToken).ConfigureAwait(false);
        CloudflareEnvelope<List<CloudflareZone>>? listed = zones.IsSuccess ? zones.Read(CloudflareJsonContext.Default.CloudflareEnvelopeListCloudflareZone, "Cloudflare") : null;
        if (listed is not { Success: true })
        {
            string reason = TryEnvelope(zones)?.FirstError ?? verifyProblem ?? $"HTTP {(int)zones.Status}";
            return new DnsCredentialCheck(false, $"Cloudflare did not accept the token for reading zones: {reason}.");
        }

        return listed.Result is { Count: > 0 }
            ? new DnsCredentialCheck(true)
            : new DnsCredentialCheck(false, "The token is valid but can read no zone; it needs Zone:Read and DNS:Edit on the zones to issue for.");
    }

    private static bool Holds(CloudflareRecord record, string value) =>
        record.Id is { Length: > 0 } && string.Equals(DnsNames.UnquoteTxt(record.Content ?? string.Empty), value, StringComparison.Ordinal);

    private static Uri Url(string relative) => new(Api, relative);

    private DnsApiClient Open() =>
        new(
            _handlers.CreateAuthenticatingHandler(_credential, SecretInjection.Bearer([ApiHost]), _options.Transport),
            "Cloudflare",
            _options,
            _time,
            _logger);

    private async Task<string> FindZoneAsync(DnsApiClient api, string name, CancellationToken cancellationToken)
    {
        foreach (string candidate in DnsNames.ZoneCandidates(name))
        {
            if (_zones.TryGetValue(candidate, out string? known))
            {
                return known;
            }

            DnsApiResponse response = await api.SendAsync(HttpMethod.Get, Url($"zones?name={Uri.EscapeDataString(candidate)}&per_page=5"), null, cancellationToken).ConfigureAwait(false);
            CloudflareEnvelope<List<CloudflareZone>>? listed = response.IsSuccess ? response.Read(CloudflareJsonContext.Default.CloudflareEnvelopeListCloudflareZone, "Cloudflare") : null;
            if (listed is not { Success: true })
            {
                throw api.Failure("look up the zone", response, TryEnvelope(response)?.FirstError);
            }

            if (listed.Result?.FirstOrDefault(z => z.Id is { Length: > 0 } && string.Equals(z.Name, candidate, StringComparison.OrdinalIgnoreCase)) is { } zone)
            {
                _zones[candidate] = zone.Id!;
                return zone.Id!;
            }
        }

        throw new DnsProviderException($"Cloudflare has no zone for {name} that this token can see.", isTransient: false);
    }

    private static async Task<List<CloudflareRecord>> ListAsync(DnsApiClient api, string zone, string type, string name, CancellationToken cancellationToken)
    {
        List<CloudflareRecord> all = [];
        for (int page = 1; page <= MaxPages; page++)
        {
            string query = string.Create(CultureInfo.InvariantCulture, $"zones/{zone}/dns_records?type={type}&name={Uri.EscapeDataString(name)}&per_page={PageSize}&page={page}");
            DnsApiResponse response = await api.SendAsync(HttpMethod.Get, Url(query), null, cancellationToken).ConfigureAwait(false);
            CloudflareEnvelope<List<CloudflareRecord>>? listed = response.IsSuccess ? response.Read(CloudflareJsonContext.Default.CloudflareEnvelopeListCloudflareRecord, "Cloudflare") : null;
            if (listed is not { Success: true })
            {
                throw api.Failure($"list the {type} records", response, TryEnvelope(response)?.FirstError);
            }

            all.AddRange(listed.Result ?? []);
            if (listed.ResultInfo is not { } info || page >= info.TotalPages)
            {
                return all;
            }
        }

        // Going on with a cut-off list could miss the record to delete, or duplicate one.
        throw new DnsProviderException($"{name} has more than {MaxPages * PageSize} {type} records in Cloudflare; refusing to guess.", isTransient: false);
    }

    /// <summary>The error envelope of a refusal, or null when the body is not one (a refusal's text is a bonus, not a requirement).</summary>
    private static CloudflareEnvelope<object>? TryEnvelope(DnsApiResponse response)
    {
        try
        {
            return JsonSerializer.Deserialize(response.Body, CloudflareJsonContext.Default.CloudflareEnvelopeObject);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Cloudflare: TXT record at {Name} created.")]
    private partial void LogCreated(string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cloudflare: TXT record at {Name} deleted.")]
    private partial void LogDeleted(string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cloudflare: TXT record at {Name} was already gone.")]
    private partial void LogAlreadyGone(string name);
}
