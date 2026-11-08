using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Paduraksa.Admin.Auth;
using Paduraksa.Admin.Certificates;
using Paduraksa.Admin.Json;
using Paduraksa.Contracts.Certificates;

namespace Paduraksa.Admin.Api;

/// <summary>
/// The local CA root's download endpoints (PLAN §3, §12.1): the first-run wizard offers the root so the owner trusts it
/// once and the admin UI is then trusted by their browsers.
/// </summary>
/// <remarks>
/// <para>
/// All three are anonymous (<see cref="AdminAuthEndpointExtensions.AllowAnonymousAdmin"/>) and reachable while first-run
/// setup is incomplete (<see cref="AdminPipelineHooks.SetupAllowedPathPrefixes"/> lists the prefix by default): the wizard
/// asks for them before an Owner exists. They serve the <b>public</b> root certificate only, from <see cref="CaRootSource"/>
/// (published by the core host's admin certificate service) — never a key, never any other certificate, never a chain.
/// </para>
/// <para>
/// <c>GET /api/v1/ca/root.pem</c> (PEM, <c>application/x-pem-file</c>), <c>GET /api/v1/ca/root.crt</c> (DER,
/// <c>application/x-x509-ca-cert</c>, the type Android and iOS offer to install) and <c>GET /api/v1/ca</c> (the
/// fingerprint, subject and validity as JSON). Each also answers <c>HEAD</c>. With no local CA yet each answers 404 with
/// code <c>ca.notFound</c>; the process creates it shortly after startup.
/// </para>
/// </remarks>
internal static class CaEndpoint
{
    /// <summary>The path prefix of the endpoints, relative to <c>/api/v1</c>.</summary>
    public const string Prefix = "/ca";

    /// <summary>The path prefix of the endpoints as the setup gate lists it.</summary>
    public const string PathPrefix = "/api/v1" + Prefix;

    /// <summary>The problem's code when the process has no local CA yet.</summary>
    private const string PemContentType = "application/x-pem-file";
    private const string DerContentType = "application/x-x509-ca-cert";

    private static readonly string[] GetAndHead = [HttpMethods.Get, HttpMethods.Head];

    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        RouteGroupBuilder ca = api.MapGroup(Prefix).WithTags("CA");
        ca.MapMethods("/root.pem", GetAndHead, GetRootPem).WithName("GetCaRootPem").WithSummary("Download the local CA root as PEM")
            .WithDescription("Anonymous. The public root certificate only, so the owner can trust it once and the admin UI is then trusted by their browsers.")
            .Produces(StatusCodes.Status200OK, contentType: PemContentType).ProducesApiProblems(404)
            .AllowAnonymousAdmin();
        ca.MapMethods("/root.crt", GetAndHead, GetRootCrt).WithName("GetCaRootCrt").WithSummary("Download the local CA root as DER")
            .WithDescription("Anonymous. application/x-x509-ca-cert, the type Android and iOS offer to install. The public root certificate only.")
            .Produces(StatusCodes.Status200OK, contentType: DerContentType).ProducesApiProblems(404)
            .AllowAnonymousAdmin();
        ca.MapMethods(string.Empty, GetAndHead, GetRoot).WithName("GetCaRoot").WithSummary("Describe the local CA root")
            .WithDescription("Anonymous. The public root's SHA-256 fingerprint, subject and validity: what the wizard shows next to the download, so the owner can check what they are asked to trust.")
            .Produces<CaRootCertificate>().ProducesApiProblems(404)
            .AllowAnonymousAdmin();
    }

    private static Task GetRootPem(HttpContext http, CaRootSource roots) =>
        roots.Current is { } root
            ? Results.Bytes(root.Pem, PemContentType, "paduraksa-root-ca.pem").ExecuteAsync(http)
            : NoRoot(http);

    private static Task GetRootCrt(HttpContext http, CaRootSource roots) =>
        roots.Current is { } root
            ? Results.Bytes(root.Der, DerContentType, "paduraksa-root-ca.crt").ExecuteAsync(http)
            : NoRoot(http);

    private static Task GetRoot(HttpContext http, CaRootSource roots) =>
        roots.Current is { } root
            ? EndpointSupport.Json(
                new CaRootCertificate(root.Sha256Fingerprint, root.Subject, root.NotBefore, root.NotAfter),
                AdminJsonContext.Default.CaRootCertificate).ExecuteAsync(http)
            : NoRoot(http);

    private static Task NoRoot(HttpContext http) =>
        ApiProblems.Problem(
            StatusCodes.Status404NotFound,
            "The internal CA root does not exist yet; it is created when the server starts.",
            instance: http.Request.Path,
            code: ApiCodes.Ca.NotFound).ExecuteAsync(http);
}
