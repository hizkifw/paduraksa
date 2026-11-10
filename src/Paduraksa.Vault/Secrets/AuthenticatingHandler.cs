using System.Security.Cryptography;
using System.Text;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Vault.Secrets;

/// <summary><see cref="ISecretHttpHandlerFactory"/>: hands out <see cref="AuthenticatingHandler"/>s over a vault-built transport.</summary>
internal sealed class SecretHttpHandlerFactory(VaultRuntime runtime, SecretReader reader) : ISecretHttpHandlerFactory
{
    public HttpMessageHandler CreateAuthenticatingHandler(SecretId id, SecretInjection how, SecretHttpTransportOptions transport)
    {
        ArgumentNullException.ThrowIfNull(how);
        ArgumentNullException.ThrowIfNull(transport);
        return new AuthenticatingHandler(reader, id, how, runtime.CreateTerminalHandler(transport));
    }
}

/// <summary>
/// The whole transport of a DNS credential: on every request it
/// checks where the request is going, reads the secret inside the vault, puts the credential on a <i>clone</i> of the
/// request, sends the clone over its own terminal handler and gives the response back with <c>RequestMessage</c> set to the
/// caller's request.
/// </summary>
/// <remarks>
/// <para>
/// It derives from <see cref="HttpMessageHandler"/>, not <see cref="DelegatingHandler"/>, so there is no <c>InnerHandler</c>
/// to swap, and the terminal handler is held privately behind an <see cref="HttpMessageInvoker"/> that nothing can reach.
/// The caller's request never carries the credential (not while it is sent, not afterwards through
/// <c>response.RequestMessage</c>), and a caller-set header of the credential's name is replaced on the clone.
/// </para>
/// <para>
/// Refusals (<see cref="SecretTransportRefusedException"/>, before any I/O to the destination): not https; a destination (normalised
/// <c>IdnHost</c>, plus the port unless it is 443) that is not in both <see cref="SecretInjection.AllowedHosts"/> and the
/// secret's own allowed hosts; a <c>Host</c> header that differs from the URL's destination; a secret that is gone or is not a <see cref="SecretKind.DnsApiToken"/>; a value that cannot be a header
/// value. Redirects are never followed, because the terminal handler is built not to, so a 3xx comes back as it is.
/// </para>
/// <para>
/// The credential does become a managed string while the header is built (HTTP headers are strings), and the string cannot
/// be zeroed: the narrowest the exposure can be made is the lifetime of that request.
/// </para>
/// </remarks>
internal sealed class AuthenticatingHandler : HttpMessageHandler
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly SecretReader _reader;

    private readonly SecretId _id;

    private readonly SecretInjection _how;

    private readonly HttpMessageInvoker _terminal;

    public AuthenticatingHandler(SecretReader reader, SecretId id, SecretInjection how, HttpMessageHandler terminal)
    {
        _reader = reader;
        _id = id;
        _how = how;
        _terminal = new HttpMessageInvoker(terminal, disposeHandler: true);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequestUri is not { IsAbsoluteUri: true } uri || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw Refused("the request is not an absolute https URL");
        }

        var host = AuthorityOf(uri);
        RequireHostHeaderToMatch(request, host);
        if (!_how.AllowedHosts.Contains(host, StringComparer.Ordinal))
        {
            throw Refused($"the destination '{host}' is not one the caller allowed");
        }

        string headerName;
        string headerValue;
        using (var secret = await _reader.OpenAsync(_id, includePrevious: false, cancellationToken).ConfigureAwait(false))
        {
            if (secret is null || secret.Row.Kind != SecretKind.DnsApiToken)
            {
                throw Refused("the secret does not exist or is not a DNS API token");
            }

            if (!secret.Row.AllowedHosts.Contains(host, StringComparer.Ordinal))
            {
                throw Refused($"the destination '{host}' is not one the secret allows");
            }

            (headerName, headerValue) = Credential(_how, secret.Current.Span);
        }

        var clone = Clone(request, uri);
        try
        {
            clone.Headers.Remove(headerName);
            clone.Headers.TryAddWithoutValidation(headerName, headerValue);

            var response = await _terminal.SendAsync(clone, cancellationToken).ConfigureAwait(false);
            response.RequestMessage = request;
            return response;
        }
        finally
        {
            // The content belongs to the caller's request: a disposed clone must not take it with it.
            clone.Content = null;
            clone.Dispose();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _terminal.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// The destination as the allow lists write it: the normalised <c>IdnHost</c>, and the port unless it is 443 (so a request
    /// to another port is allowed only by an entry that names it, <c>host:8443</c>).
    /// </summary>
    private static string AuthorityOf(Uri uri)
    {
        try
        {
            var host = VaultHostNames.Normalize(uri.IdnHost);
            return uri.Port == VaultHostNames.DefaultHttpsPort ? host : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{host}:{uri.Port}");
        }
        catch (ArgumentException)
        {
            throw Refused("the request's host is not a plain host name");
        }
    }

    /// <summary>
    /// A <c>Host</c> header the caller set is what the socket handler would use for the TLS server name, the certificate
    /// check and the HTTP <c>Host</c>, so it could send the credential to a different server than the one the allow lists
    /// were checked against ("domain fronting"). It is therefore never copied to the clone, and a request that sets one that
    /// differs from the URL's destination is refused.
    /// </summary>
    private static void RequireHostHeaderToMatch(HttpRequestMessage request, string destination)
    {
        if (request.Headers.Host is not { } header)
        {
            return;
        }

        string normalised;
        try
        {
            normalised = VaultHostNames.NormalizeAuthority(header);
        }
        catch (ArgumentException)
        {
            throw Refused("the Host header is not a plain host name");
        }

        if (!string.Equals(normalised, destination, StringComparison.Ordinal))
        {
            throw Refused("the Host header names a different destination than the URL");
        }
    }

    private static SecretTransportRefusedException Refused(string reason) =>
        new($"The authenticating handler refused the request: {reason}.");

    /// <summary>The header to set and its value, from the secret (the only place the value is turned into text).</summary>
    private static (string Name, string Value) Credential(SecretInjection how, ReadOnlySpan<byte> secret)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(secret);
        }
        catch (DecoderFallbackException)
        {
            throw Refused("the secret is not text that can go in a header");
        }

        if (text.Length == 0 || text.Any(char.IsControl) || text.Trim().Length != text.Length)
        {
            throw Refused("the secret is not text that can go in a header");
        }

        switch (how.Kind)
        {
            case SecretInjectionKind.Authorization:
                return ("Authorization", $"{how.Scheme} {text}");
            case SecretInjectionKind.Header:
                return (how.HeaderName!, text);
            case SecretInjectionKind.BasicAuth:
                var pair = Encoding.UTF8.GetBytes($"{how.Username}:{text}");
                try
                {
                    return ("Authorization", "Basic " + Convert.ToBase64String(pair));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(pair);
                }

            default:
                throw Refused("unknown injection kind");
        }
    }

    private static HttpRequestMessage Clone(HttpRequestMessage request, Uri uri)
    {
        var clone = new HttpRequestMessage(request.Method, uri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
            Content = request.Content,
        };
        foreach (var header in request.Headers)
        {
            // Never Host (see RequireHostHeaderToMatch): the clone's comes from its URL. Options are not copied either:
            // they are in-process state of the caller's pipeline, and the clone goes straight to the vault's own transport.
            if (!string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }
}
