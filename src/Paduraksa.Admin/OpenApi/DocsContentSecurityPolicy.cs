using System.Security.Cryptography;

using Microsoft.AspNetCore.Http;

namespace Paduraksa.Admin.OpenApi;

/// <summary>
/// The content security policy of the API reference page: only what Scalar needs, and only from the admin app itself. Scripts are the
/// embedded bundle (<c>'self'</c>) and Scalar's inline bootstrap, which carries a per-request nonce; Scalar writes inline styles, so styles may be
/// inline; images and fonts may be <c>data:</c> URLs; the page may call this app (the OpenAPI document and its try-it requests) and nothing else.
/// Nothing may be framed, embedded, loaded from another origin or submitted elsewhere.
/// </summary>
internal static class DocsContentSecurityPolicy
{
    private const string NonceKey = "paduraksa.docs.nonce";

    /// <summary>The nonce for this request's inline script, created on first use.</summary>
    public static string NonceOf(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.Items[NonceKey] is not string nonce)
        {
            nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            http.Items[NonceKey] = nonce;
        }

        return nonce;
    }

    /// <summary>True for the reference page and its assets.</summary>
    public static bool Applies(PathString path) => path.StartsWithSegments(AdminOpenApi.DocsPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>The header value for this request.</summary>
    public static string For(HttpContext http) =>
        "default-src 'none'; " +
        $"script-src 'self' 'nonce-{NonceOf(http)}'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self' data:; " +
        "connect-src 'self'; " +
        "worker-src 'self' blob:; " +
        "base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
}
