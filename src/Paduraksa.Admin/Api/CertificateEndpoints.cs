using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;
using Paduraksa.Admin.Auth;
using Paduraksa.Admin.Json;
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Issuance;
using Paduraksa.Contracts.Api;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Api;

/// <summary>
/// The certificate inventory and everything that makes or removes a certificate. Reads return public data only (the chain, never a key). Keys
/// are made inside the vault, and an upload is streamed from the request into the vault's import: this layer never holds the key or the
/// file in a buffer or a string.
/// </summary>
internal static class CertificateEndpoints
{
    private const int MaxPasswordBytes = 1024;
    private const int MaxLabelBytes = 4 * CertificateService.MaxLabelLength;

    public static void Map(RouteGroupBuilder api)
    {
        var certificates = api.MapGroup("/certificates").WithTags("Certificates");
        certificates.MapGet("/", ListAsync).WithName("ListCertificates").WithSummary("List certificates")
            .WithDescription("Public data only. Filters: source, status (their names) and name (the certificates covering a host name).")
            .Produces<Page<CertificateSummary>>().ProducesApiProblems(400).RequirePermission(Permission.CertificatesRead);
        certificates.MapGet("/{id:CertificateId}", GetAsync).WithName("GetCertificate").WithSummary("Get a certificate with its public chain")
            .Produces<CertificateDetail>().ProducesApiProblems(404).RequirePermission(Permission.CertificatesRead);

        certificates.MapPost("/", UploadAsync).WithName("UploadCertificate").WithSummary("Upload a certificate with its key (PEM or PFX)")
            .WithDescription("multipart/form-data. Parts, in this order: 'password' (optional), 'label' (optional), then 'file': the PEM (PKCS#8 key) or PFX, at most 1 MiB. The file is streamed into the vault; the key is never returned.")
            .Accepts<UploadForm>("multipart/form-data").Produces<CertificateOperationResult>(201).ProducesApiProblems(400, 413, 415, 422)
            .WithAdminBodyLimit(ApiEndpointConventions.UploadBodyLimit).AcceptsAdminContentTypes("multipart/form-data")
            .RequirePermission(Permission.CertificatesWrite);
        certificates.MapPut("/{id:CertificateId}", ReplaceUploadAsync).WithName("ReplaceUploadedCertificate").WithSummary("Replace an uploaded certificate under the same ID")
            .WithDescription("Like the upload, for a renewed certificate: key and chain swap together, so bindings that name the certificate keep working.")
            .Accepts<UploadForm>("multipart/form-data").Produces<CertificateOperationResult>().ProducesApiProblems(400, 404, 413, 415, 422)
            .WithAdminBodyLimit(ApiEndpointConventions.UploadBodyLimit).AcceptsAdminContentTypes("multipart/form-data")
            .RequirePermission(Permission.CertificatesWrite);

        certificates.MapPost("/self-signed", SelfSignedAsync).WithName("IssueSelfSignedCertificate").WithSummary("Issue a self-signed certificate")
            .Accepts<SelfSignedCertificateRequest>("application/json").Produces<CertificateOperationResult>(201).ProducesApiProblems(400, 415, 422)
            .RequirePermission(Permission.CertificatesWrite);
        certificates.MapPost("/local-ca", LocalCaAsync).WithName("CreateLocalCa").WithSummary("Create the local CA root")
            .WithDescription("There is one local CA: asking again returns the existing root. The body is optional.")
            .Accepts<LocalCaRequest>("application/json").Produces<CertificateOperationResult>(201).ProducesApiProblems(400, 415, 422)
            .RequirePermission(Permission.CertificatesWrite);
        certificates.MapGet("/local-ca", LocalCaStatusAsync).WithName("GetLocalCa").WithSummary("The local CA: its root, what it is limited to, and the roots a rotation left")
            .Produces<LocalCaStatus>().RequirePermission(Permission.CertificatesRead);
        certificates.MapPost("/local-ca/rotate", RotateLocalCaAsync).WithName("RotateLocalCa").WithSummary("Replace the local CA root")
            .WithDescription("Makes a new root (limited to the private-use domains, the private ranges and the names in the body) and destroys the old root's key in the same step. Devices must trust the new root before what it signs works for them; what the old root signed keeps working, and the old root stays downloadable, until it is retired (DELETE its certificate once nothing it signed is left). The admin listener's certificate is issued again at once. The body is optional.")
            .Accepts<LocalCaRotateRequest>("application/json").Produces<CertificateOperationResult>().ProducesApiProblems(400, 415, 422)
            .RequirePermission(Permission.CertificatesWrite);
        certificates.MapPost("/local-ca/reissue", ReissueLocalCaLeavesAsync).WithName("ReissueLocalCaCertificates").WithSummary("Re-issue what a retiring local CA root signed")
            .WithDescription("Issues every certificate a root replaced by a rotation signed again, from the current root, under the same IDs. Certificates whose names the new root does not permit are left alone and listed in failures.")
            .Produces<LocalCaReissueResult>().RequirePermission(Permission.CertificatesWrite);
        certificates.MapPost("/local-ca/leaf", LocalCaLeafAsync).WithName("IssueLocalCaCertificate").WithSummary("Issue a certificate from the local CA")
            .Accepts<LocalCaLeafRequest>("application/json").Produces<CertificateOperationResult>(201).ProducesApiProblems(400, 415, 422)
            .RequirePermission(Permission.CertificatesWrite);
        certificates.MapPost("/{id:CertificateId}/reissue", ReissueAsync).WithName("ReissueCertificate").WithSummary("Re-issue a self-signed or local-CA certificate with a new key")
            .WithDescription("Same ID and names, so bindings survive. The body is optional.")
            .Accepts<ReissueRequest>("application/json").Produces<CertificateOperationResult>().ProducesApiProblems(400, 404, 415, 422)
            .RequirePermission(Permission.CertificatesWrite);
        certificates.MapDelete("/{id:CertificateId}", DeleteAsync).WithName("DeleteCertificate").WithSummary("Delete a certificate and its key")
            .WithDescription("Refused (422) while a host still binds it specifically. 204 when it is gone; 200 with the result when there are warnings to report (a vault key that could not be removed).")
            .Produces(204).Produces<CertificateOperationResult>().ProducesApiProblems(404, 422).RequirePermission(Permission.CertificatesWrite);

        certificates.MapPost("/acme", RequestAcmeAsync).WithName("RequestAcmeCertificate").WithSummary("Request a certificate from an ACME CA")
            .WithDescription("Runs the first issuance to the end and answers when it is issued or has failed (502, and the scheduler retries; the problem's instance is the certificate). The issuance is not tied to the request: a client that gives up does not cancel it, and GET /certificates/{id}/order shows its progress. A DNS-01 order waits for propagation and can outlast a typical client timeout, so give the request minutes, or poll the order instead.")
            .Accepts<CertificateIssueRequest>("application/json").Produces<IssuanceResult>(201).ProducesApiProblems(400, 409, 415, 422, 502)
            .RequirePermission(Permission.CertificatesWrite);
        certificates.MapPost("/{id:CertificateId}/renew", RenewAsync).WithName("RenewCertificateNow").WithSummary("Renew an ACME certificate now")
            .WithDescription("Like the request: runs to the end whether or not the client waits, and answers synchronously when it finishes; GET /certificates/{id}/order shows progress.")
            .Produces<IssuanceResult>().ProducesApiProblems(404, 409, 422, 502).RequirePermission(Permission.CertificatesWrite);
        certificates.MapGet("/{id:CertificateId}/order", GetOrderAsync).WithName("GetCertificateOrder").WithSummary("The ACME order and renewal state of a certificate")
            .Produces<AcmeOrderSummary>().ProducesApiProblems(404).RequirePermission(Permission.CertificatesRead);
        api.MapGet("/acme-orders", ListOrdersAsync).WithName("ListAcmeOrders").WithTags("Certificates").WithSummary("The ACME order state of every ACME certificate")
            .Produces<Page<AcmeOrderSummary>>().ProducesApiProblems(400).RequirePermission(Permission.CertificatesRead);
    }

    private static async Task<IResult> ListAsync(HttpContext http, CertificateService certificates, CancellationToken ct)
    {
        CertificateSource? source = null;
        CertificateStatus? status = null;
        try
        {
            if (http.Request.Query["source"].ToString() is { Length: > 0 } sourceName)
            {
                source = ModelNames.ParseCertificateSource(sourceName);
            }

            if (http.Request.Query["status"].ToString() is { Length: > 0 } statusName)
            {
                status = ModelNames.ParseCertificateStatus(statusName);
            }
        }
        catch (FormatException)
        {
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "source and status must be names the API uses, e.g. source=acme, status=active.");
        }

        var name = http.Request.Query["name"].ToString() is { Length: > 0 } covers ? covers : null;
        var list = await certificates.ListAsync(new CertificateQuery(source, status, name), ct).ConfigureAwait(false);
        return Pagination.Respond(http, list, c => c.Id, AdminJsonContext.Default.PageCertificateSummary);
    }

    private static async Task<IResult> GetAsync(CertificateId id, CertificateService certificates, CancellationToken ct) =>
        await certificates.GetAsync(id, ct).ConfigureAwait(false) is { } detail
            ? EndpointSupport.Json(detail, AdminJsonContext.Default.CertificateDetail)
            : ApiProblems.Problem(StatusCodes.Status404NotFound, "There is no such certificate.");

    private static Task<IResult> UploadAsync(HttpContext http, AdminCaller caller, CertificateOperations operations, CancellationToken ct) =>
        UploadCoreAsync(http, caller, operations, null, ct);

    private static Task<IResult> ReplaceUploadAsync(HttpContext http, CertificateId id, AdminCaller caller, CertificateOperations operations, CancellationToken ct) =>
        UploadCoreAsync(http, caller, operations, id, ct);

    /// <summary>
    /// Reads the multipart body part by part. 'password' and 'label' are small and come first; the 'file' part's stream goes straight to the
    /// vault's import and nothing is read ahead of it. The password lives in a char array that is cleared when the request is done.
    /// </summary>
    private static async Task<IResult> UploadCoreAsync(HttpContext http, AdminCaller caller, CertificateOperations operations, CertificateId? replace, CancellationToken ct)
    {
        if (!MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var contentType)
            || !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return ApiProblems.Problem(StatusCodes.Status415UnsupportedMediaType, "Send the upload as multipart/form-data.");
        }

        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 128)
        {
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "The multipart body has no usable boundary.");
        }

        char[] password = [];
        string? label = null;
        var sawPassword = false;
        var sawLabel = false;
        try
        {
            MultipartReader reader = new(boundary, http.Request.Body) { HeadersLengthLimit = 8 * 1024, BodyLengthLimit = ApiEndpointConventions.UploadBodyLimit };
            while (await reader.ReadNextSectionAsync(ct).ConfigureAwait(false) is { } section)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                    || !disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase))
                {
                    return ApiProblems.Problem(StatusCodes.Status400BadRequest, "Every part must be form-data with a name.");
                }

                switch (HeaderUtilities.RemoveQuotes(disposition.Name).Value)
                {
                    case "password":
                        if (sawPassword)
                        {
                            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "The 'password' part is given twice.");
                        }

                        sawPassword = true;
                        var passwordBytes = await ReadSmallAsync(section.Body, MaxPasswordBytes, ct).ConfigureAwait(false);
                        if (passwordBytes is null)
                        {
                            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "The password part is too long.");
                        }

                        password = Encoding.UTF8.GetChars(passwordBytes);
                        CryptographicOperations.ZeroMemory(passwordBytes);
                        break;
                    case "label":
                        if (sawLabel)
                        {
                            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "The 'label' part is given twice.");
                        }

                        sawLabel = true;
                        var labelBytes = await ReadSmallAsync(section.Body, MaxLabelBytes, ct).ConfigureAwait(false);
                        if (labelBytes is null)
                        {
                            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "The label part is too long.");
                        }

                        label = labelBytes.Length == 0 ? null : Encoding.UTF8.GetString(labelBytes);
                        break;
                    case "file":
                        var result = await operations.UploadAsync(section.Body, password, label, replace, caller.Origin, ct).ConfigureAwait(false);
                        return OperationResult(result, replace is null ? MutationKind.Create : MutationKind.Change);
                    default:
                        return ApiProblems.Problem(StatusCodes.Status400BadRequest, "The parts are 'password', 'label' and 'file'.");
                }
            }

            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "The upload has no 'file' part (and a password or label must come before it).");
        }
        catch (InvalidDataException)
        {
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "The multipart body is malformed or a part is too large.");
        }
        finally
        {
            Array.Clear(password);
        }
    }

    private static async Task<IResult> SelfSignedAsync(HttpContext http, AdminCaller caller, CertificateOperations operations, CancellationToken ct)
    {
        var body = await ApiBody.ReadJsonAsync(http, AdminJsonContext.Default.SelfSignedCertificateRequest).ConfigureAwait(false);
        return body.Ok
            ? OperationResult(await operations.IssueSelfSignedAsync(body.Value!, caller.Origin, ct).ConfigureAwait(false), MutationKind.Create)
            : body.Problem!;
    }

    private static async Task<IResult> LocalCaAsync(HttpContext http, AdminCaller caller, CertificateOperations operations, CancellationToken ct)
    {
        LocalCaRequest request = new();
        if (HasBody(http))
        {
            var body = await ApiBody.ReadJsonAsync(http, AdminJsonContext.Default.LocalCaRequest).ConfigureAwait(false);
            if (!body.Ok)
            {
                return body.Problem!;
            }

            request = body.Value!;
        }

        return OperationResult(await operations.EnsureLocalCaAsync(request, caller.Origin, ct).ConfigureAwait(false), MutationKind.Create);
    }

    private static async Task<IResult> LocalCaStatusAsync(CertificateOperations operations, CancellationToken ct) =>
        EndpointSupport.Json(await operations.GetLocalCaAsync(ct).ConfigureAwait(false), AdminJsonContext.Default.LocalCaStatus);

    private static async Task<IResult> RotateLocalCaAsync(HttpContext http, AdminCaller caller, CertificateOperations operations, CancellationToken ct)
    {
        LocalCaRotateRequest request = new();
        if (HasBody(http))
        {
            var body = await ApiBody.ReadJsonAsync(http, AdminJsonContext.Default.LocalCaRotateRequest).ConfigureAwait(false);
            if (!body.Ok)
            {
                return body.Problem!;
            }

            request = body.Value!;
        }

        return OperationResult(await operations.RotateLocalCaAsync(request, caller.Origin, ct).ConfigureAwait(false), MutationKind.Change);
    }

    private static async Task<IResult> ReissueLocalCaLeavesAsync(AdminCaller caller, CertificateOperations operations, CancellationToken ct) =>
        EndpointSupport.Json(await operations.ReissueLocalCaLeavesAsync(caller.Origin, ct).ConfigureAwait(false), AdminJsonContext.Default.LocalCaReissueResult);

    private static async Task<IResult> LocalCaLeafAsync(HttpContext http, AdminCaller caller, CertificateOperations operations, CancellationToken ct)
    {
        var body = await ApiBody.ReadJsonAsync(http, AdminJsonContext.Default.LocalCaLeafRequest).ConfigureAwait(false);
        return body.Ok
            ? OperationResult(await operations.IssueFromLocalCaAsync(body.Value!, caller.Origin, ct).ConfigureAwait(false), MutationKind.Create)
            : body.Problem!;
    }

    private static async Task<IResult> ReissueAsync(HttpContext http, AdminCaller caller, CertificateId id, CertificateOperations operations, CancellationToken ct)
    {
        ReissueRequest? request = null;
        if (HasBody(http))
        {
            var body = await ApiBody.ReadJsonAsync(http, AdminJsonContext.Default.ReissueRequest).ConfigureAwait(false);
            if (!body.Ok)
            {
                return body.Problem!;
            }

            request = body.Value;
        }

        return OperationResult(await operations.ReissueAsync(id, request, caller.Origin, ct).ConfigureAwait(false), MutationKind.Change);
    }

    private static async Task<IResult> DeleteAsync(HttpContext http, AdminCaller caller, CertificateId id, CertificateOperations operations, CancellationToken ct)
    {
        var result = await operations.DeleteAsync(id, caller.Origin, ct).ConfigureAwait(false);
        // Gone is 204; when something needs the operator's eye (the vault key could not be removed) the result is the body.
        return result is { Status: CertificateOperationStatus.Completed, Warnings.Count: 0 } ? Results.NoContent() : OperationResult(result, MutationKind.Change);
    }

    private static async Task<IResult> RequestAcmeAsync(HttpContext http, AdminCaller caller, AcmeIssuanceService issuance, IHostApplicationLifetime lifetime)
    {
        var body = await ApiBody.ReadJsonAsync(http, AdminJsonContext.Default.CertificateIssueRequest).ConfigureAwait(false);
        return body.Ok
            ? IssuanceAnswer(http, await issuance.RequestCertificateAsync(body.Value!, caller.Origin, lifetime.ApplicationStopping).ConfigureAwait(false), MutationKind.Create)
            : body.Problem!;
    }

    private static async Task<IResult> RenewAsync(HttpContext http, AdminCaller caller, CertificateId id, AcmeIssuanceService issuance, IHostApplicationLifetime lifetime) =>
        IssuanceAnswer(http, await issuance.RenewAsync(id, caller.Origin, lifetime.ApplicationStopping).ConfigureAwait(false), MutationKind.Change);

    private static async Task<IResult> GetOrderAsync(CertificateId id, AcmeIssuanceService issuance, CancellationToken ct) =>
        await issuance.GetOrderAsync(id, ct).ConfigureAwait(false) is { } order
            ? EndpointSupport.Json(order, AdminJsonContext.Default.AcmeOrderSummary)
            : ApiProblems.Problem(StatusCodes.Status404NotFound, "This certificate has no ACME order.");

    private static async Task<IResult> ListOrdersAsync(HttpContext http, AcmeIssuanceService issuance, CancellationToken ct) =>
        Pagination.Respond(http, await issuance.ListOrdersAsync(ct).ConfigureAwait(false), o => o.CertificateId, AdminJsonContext.Default.PageAcmeOrderSummary);

    private static IResult OperationResult(CertificateOperationResult result, MutationKind kind)
    {
        var status = ApiStatusMapping.Of(result.Status, kind);
        if (!ApiStatusMapping.IsSuccess(status))
        {
            return ApiProblems.Problem(status, result.Status switch
            {
                CertificateOperationStatus.Invalid => "The operation was refused; see errors.",
                CertificateOperationStatus.NotFound => "There is no such certificate.",
                CertificateOperationStatus.Forbidden => "You may not change certificates.",
                _ => "The config store is unavailable, so certificates can't be changed now.",
            }, null, result.Errors);
        }

        return EndpointSupport.Located(EndpointSupport.Json(result, AdminJsonContext.Default.CertificateOperationResult, status), status == StatusCodes.Status201Created && result.Certificate is { } certificate ? EndpointSupport.Path("certificates", certificate.Id) : null);
    }

    private static IResult IssuanceAnswer(HttpContext http, IssuanceResult result, MutationKind kind)
    {
        var status = ApiStatusMapping.Of(result.Outcome, kind);
        if (!ApiStatusMapping.IsSuccess(status))
        {
            var instance = result.CertificateId is { } id ? EndpointSupport.Path("certificates", id) : null;
            return ApiProblems.Problem(status, result.Outcome switch
            {
                IssuanceOutcome.Failed => result.Message is { Length: > 0 } message ? $"The CA did not issue the certificate: {message}. The renewal scheduler retries." : "The CA did not issue the certificate. The renewal scheduler retries.",
                IssuanceOutcome.InProgress => "This certificate is being issued already.",
                IssuanceOutcome.Invalid => "The request was refused; see errors.",
                IssuanceOutcome.NotFound => "There is no such certificate or order.",
                IssuanceOutcome.Forbidden => "You may not request or renew certificates.",
                _ => "The config store is unavailable, so certificates can't be issued now.",
            }, instance, result.Errors);
        }

        return EndpointSupport.Located(EndpointSupport.Json(result, AdminJsonContext.Default.IssuanceResult, status), status == StatusCodes.Status201Created && result.CertificateId is { } created ? EndpointSupport.Path("certificates", created) : null);
    }

    private static bool HasBody(HttpContext http) =>
        http.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody ?? http.Request.ContentLength > 0;

    private static async Task<byte[]?> ReadSmallAsync(Stream stream, int max, CancellationToken ct)
    {
        var buffer = new byte[max + 1];
        var total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > max)
            {
                CryptographicOperations.ZeroMemory(buffer);
                return null;
            }
        }

        var result = buffer[..total];
        CryptographicOperations.ZeroMemory(buffer);
        return result;
    }

    /// <summary>The upload form, for the API description only.</summary>
    internal sealed record UploadForm(string? Password, string? Label, byte[] File);
}
