using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Paduraksa.Admin.Auth;
using Paduraksa.Admin.Json;
using Paduraksa.Application.Issuance;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Api;

/// <summary>
/// ACME accounts and DNS providers: the two places a secret goes in. A DNS credential or an external-account-binding key is stored in the
/// vault the moment it arrives, and only the secret's ID (or nothing) is ever kept or answered; there is no endpoint that returns a secret, and
/// no response type that could carry one.
/// </summary>
internal static class ProviderEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        RouteGroupBuilder accounts = api.MapGroup("/acme-accounts").WithTags("ACME accounts");
        accounts.MapGet("/", ListAccountsAsync).WithName("ListAcmeAccounts").WithSummary("List ACME accounts")
            .Produces<IReadOnlyList<AcmeAccountSummary>>().RequirePermission(Permission.ProvidersRead);
        accounts.MapGet("/{id:AcmeAccountId}", GetAccountAsync).WithName("GetAcmeAccount").WithSummary("Get an ACME account")
            .Produces<AcmeAccountSummary>().ProducesApiProblems(404).RequirePermission(Permission.ProvidersRead);
        accounts.MapPost("/", CreateAccountAsync).WithName("CreateAcmeAccount").WithSummary("Register an ACME account")
            .WithDescription("The vault creates the account key. termsOfServiceAgreed records that the operator accepted the CA's terms; most CAs refuse an account without it. For CAs with external account binding give eabKid and eabHmacKey (base64url, as the CA shows it): the key goes into the vault and is never returned.")
            .Accepts<CreateAcmeAccountBody>("application/json").Produces<AcmeAccountSummary>(201).ProducesApiProblems(400, 415, 422, 502)
            .RequirePermission(Permission.ProvidersWrite);
        accounts.MapDelete("/{id:AcmeAccountId}", DeactivateAccountAsync).WithName("DeactivateAcmeAccount").WithSummary("Deactivate an ACME account")
            .WithDescription("Final (RFC 8555): deactivates the account at the CA and deletes its key. Refused (422) while certificates are ordered with it.")
            .Produces<AcmeAccountSummary>().ProducesApiProblems(404, 422, 502).RequirePermission(Permission.ProvidersWrite);

        RouteGroupBuilder providers = api.MapGroup("/dns-providers").WithTags("DNS providers");
        providers.MapGet("/", ListProvidersAsync).WithName("ListDnsProviders").WithSummary("List DNS providers")
            .Produces<IReadOnlyList<DnsProviderSummary>>().RequirePermission(Permission.ProvidersRead);
        providers.MapGet("/{id:DnsProviderId}", GetProviderAsync).WithName("GetDnsProvider").WithSummary("Get a DNS provider")
            .Produces<DnsProviderSummary>().ProducesApiProblems(404).RequirePermission(Permission.ProvidersRead);
        providers.MapPost("/", CreateProviderAsync).WithName("CreateDnsProvider").WithSummary("Add a DNS provider")
            .WithDescription("The credential goes into the vault, usable only against the provider's own API host, and is never returned.")
            .Accepts<CreateDnsProviderBody>("application/json").Produces<DnsProviderSummary>(201).ProducesApiProblems(400, 415, 422)
            .RequirePermission(Permission.ProvidersWrite);
        providers.MapPut("/{id:DnsProviderId}", RotateProviderAsync).WithName("RotateDnsProviderCredential").WithSummary("Replace a DNS provider's credential")
            .WithDescription("The provider keeps its ID, so everything that uses it keeps working. The credential is never returned.")
            .Accepts<RotateDnsCredentialBody>("application/json").Produces<DnsProviderSummary>().ProducesApiProblems(400, 404, 415, 422)
            .RequirePermission(Permission.ProvidersWrite);
        providers.MapDelete("/{id:DnsProviderId}", DeleteProviderAsync).WithName("DeleteDnsProvider").WithSummary("Delete a DNS provider and its credential")
            .WithDescription("Refused (422) while certificates are ordered through it.")
            .Produces(204).ProducesApiProblems(404, 422).RequirePermission(Permission.ProvidersWrite);
        providers.MapPost("/{id:DnsProviderId}/test", TestProviderAsync).WithName("TestDnsProviderCredentials").WithSummary("Test a DNS provider's credentials")
            .WithDescription("Asks the provider to check the credential with a read-only call. A bad credential is valid=false, not an error. The test is audited (acme.dns_provider.test) with its verdict only.")
            .Produces<DnsProviderTestOutcome>().ProducesApiProblems(404).RequirePermission(Permission.ProvidersWrite);
    }

    private static async Task<IResult> ListAccountsAsync(AcmeAccountService accounts, CancellationToken ct) =>
        EndpointSupport.Json(await accounts.ListAsync(ct).ConfigureAwait(false), AdminJsonContext.Default.IReadOnlyListAcmeAccountSummary);

    private static async Task<IResult> GetAccountAsync(AcmeAccountId id, AcmeAccountService accounts, CancellationToken ct) =>
        await accounts.GetAsync(id, ct).ConfigureAwait(false) is { } account
            ? EndpointSupport.Json(account, AdminJsonContext.Default.AcmeAccountSummary)
            : ApiProblems.Problem(StatusCodes.Status404NotFound, "There is no such ACME account.");

    private static async Task<IResult> CreateAccountAsync(HttpContext http, AdminCaller caller, AcmeAccountService accounts, CancellationToken ct)
    {
        BodyResult<CreateAcmeAccountBody> body = await ApiBody.ReadJsonAsync(http, AdminJsonContext.Default.CreateAcmeAccountBody).ConfigureAwait(false);
        if (!body.Ok)
        {
            return body.Problem!;
        }

        CreateAcmeAccountBody request = body.Value!;
        byte[] key = [];
        int keyLength = 0;
        try
        {
            if (request.EabHmacKey is { } encoded)
            {
                // The wire form only: whether the request may be made, and what the binding needs, is the service's to say before it stores anything.
                string unpadded = encoded.TrimEnd('=');
                key = new byte[Base64Url.GetMaxDecodedLength(unpadded.Length)];
                if (unpadded.Length == 0 || !Base64Url.IsValid(unpadded) || !Base64Url.TryDecodeFromChars(unpadded, key, out keyLength))
                {
                    return ApiProblems.Invalid([new ConfigError("/eabHmacKey", IssuanceErrorCodes.EabIncomplete, "eabHmacKey must be base64url, as the CA shows it")]);
                }

            }

            return AccountAnswer(
                await accounts.CreateWithEabAsync(
                    new CreateAcmeAccountRequest(request.DirectoryUrl, request.Contacts ?? [], request.TermsOfServiceAgreed, request.Algorithm, request.EabKid, null, request.Label),
                    key.AsMemory(0, keyLength),
                    caller.Origin,
                    ct).ConfigureAwait(false),
                MutationKind.Create);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static async Task<IResult> DeactivateAccountAsync(HttpContext http, AdminCaller caller, AcmeAccountId id, AcmeAccountService accounts, CancellationToken ct) =>
        AccountAnswer(await accounts.DeactivateAsync(id, caller.Origin, ct).ConfigureAwait(false), MutationKind.Change);

    private static IResult AccountAnswer(AcmeAccountResult result, MutationKind kind)
    {
        int status = ApiStatusMapping.Of(result.Status, kind);
        if (ApiStatusMapping.IsSuccess(status) && result.Account is null)
        {
            return ApiProblems.Problem(StatusCodes.Status500InternalServerError, "The account change reported success without the account.");
        }

        if (!ApiStatusMapping.IsSuccess(status))
        {
            return ApiProblems.Problem(
                status,
                result.Status == IssuanceChangeStatus.Failed && result.Message is { Length: > 0 } message ? $"The CA refused or could not be reached: {message}" : ChangeDetail(result.Status, "ACME account"),
                null,
                result.Errors);
        }

        return EndpointSupport.Located(EndpointSupport.Json(result.Account!, AdminJsonContext.Default.AcmeAccountSummary, status), status == StatusCodes.Status201Created ? EndpointSupport.Path("acme-accounts", result.Account!.Id) : null);
    }

    private static async Task<IResult> ListProvidersAsync(DnsProviderService providers, CancellationToken ct) =>
        EndpointSupport.Json(await providers.ListAsync(ct).ConfigureAwait(false), AdminJsonContext.Default.IReadOnlyListDnsProviderSummary);

    private static async Task<IResult> GetProviderAsync(DnsProviderId id, DnsProviderService providers, CancellationToken ct) =>
        await providers.GetAsync(id, ct).ConfigureAwait(false) is { } provider
            ? EndpointSupport.Json(provider, AdminJsonContext.Default.DnsProviderSummary)
            : ApiProblems.Problem(StatusCodes.Status404NotFound, "There is no such DNS provider.");

    private static async Task<IResult> CreateProviderAsync(HttpContext http, AdminCaller caller, DnsProviderService providers, CancellationToken ct)
    {
        BodyResult<CreateDnsProviderBody> body = await ApiBody.ReadJsonAsync(http, AdminJsonContext.Default.CreateDnsProviderBody).ConfigureAwait(false);
        if (!body.Ok)
        {
            return body.Problem!;
        }

        byte[] credential = Encoding.UTF8.GetBytes(body.Value!.Credential);
        try
        {
            return ProviderAnswer(await providers.CreateWithCredentialAsync(body.Value.Kind, body.Value.Label, credential, caller.Origin, ct).ConfigureAwait(false), MutationKind.Create);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credential);
        }
    }

    private static async Task<IResult> RotateProviderAsync(HttpContext http, AdminCaller caller, DnsProviderId id, DnsProviderService providers, CancellationToken ct)
    {
        BodyResult<RotateDnsCredentialBody> body = await ApiBody.ReadJsonAsync(http, AdminJsonContext.Default.RotateDnsCredentialBody).ConfigureAwait(false);
        if (!body.Ok)
        {
            return body.Problem!;
        }

        byte[] credential = Encoding.UTF8.GetBytes(body.Value!.Credential);
        try
        {
            return ProviderAnswer(await providers.RotateCredentialAsync(id, credential, caller.Origin, ct).ConfigureAwait(false), MutationKind.Change);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credential);
        }
    }

    private static async Task<IResult> DeleteProviderAsync(HttpContext http, AdminCaller caller, DnsProviderId id, DnsProviderService providers, CancellationToken ct)
    {
        DnsProviderResult result = await providers.DeleteWithCredentialAsync(id, caller.Origin, ct).ConfigureAwait(false);
        return result.Status == IssuanceChangeStatus.Committed ? Results.NoContent() : ProviderAnswer(result, MutationKind.Change);
    }

    private static async Task<IResult> TestProviderAsync(HttpContext http, AdminCaller caller, DnsProviderId id, DnsProviderService providers, CancellationToken ct)
    {
        DnsProviderTestResult result = await providers.TestAsync(id, caller.Origin, ct).ConfigureAwait(false);
        return result switch
        {
            { Denied: true } => ApiProblems.Problem(StatusCodes.Status403Forbidden, "You may not test DNS provider credentials.", code: ApiCodes.Auth.PermissionRequired),
            { Found: false } => ApiProblems.Problem(StatusCodes.Status404NotFound, "There is no such DNS provider."),
            _ => EndpointSupport.Json(new DnsProviderTestOutcome(result.Valid, result.Message), AdminJsonContext.Default.DnsProviderTestOutcome),
        };
    }

    private static IResult ProviderAnswer(DnsProviderResult result, MutationKind kind)
    {
        int status = ApiStatusMapping.Of(result.Status, kind);
        if (ApiStatusMapping.IsSuccess(status) && result.Provider is null)
        {
            return ApiProblems.Problem(StatusCodes.Status500InternalServerError, "The provider change reported success without the provider.");
        }

        if (!ApiStatusMapping.IsSuccess(status))
        {
            return ApiProblems.Problem(status, ChangeDetail(result.Status, "DNS provider"), null, result.Errors);
        }

        return EndpointSupport.Located(EndpointSupport.Json(result.Provider!, AdminJsonContext.Default.DnsProviderSummary, status), status == StatusCodes.Status201Created ? EndpointSupport.Path("dns-providers", result.Provider!.Id) : null);
    }

    private static string ChangeDetail(IssuanceChangeStatus status, string what) => status switch
    {
        IssuanceChangeStatus.NotFound => $"There is no such {what}.",
        IssuanceChangeStatus.Invalid => "The request was refused; see errors.",
        IssuanceChangeStatus.Denied => $"You may not change {what}s.",
        IssuanceChangeStatus.Failed => "The CA refused or could not be reached.",
        IssuanceChangeStatus.Unavailable => "The config store is unavailable, so changes are refused now.",
        _ => "The request could not be completed.",
    };
}
