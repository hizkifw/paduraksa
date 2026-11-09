using Paduraksa.Core.Model;

namespace Paduraksa.Core.Vault;

/// <summary>
/// The trick that keeps DNS credentials out of provider code: the vault returns a handler that adds the
/// credential to each outgoing request <i>inside the vault assembly</i>. A DNS provider client in <c>Paduraksa.Acme</c>
/// builds its <see cref="HttpClient"/> over this handler; the Cloudflare token is read and attached in the vault and never
/// exists as a value in Acme code.
/// </summary>
/// <remarks>
/// <para>
/// <b>The handler is the whole transport.</b> Nothing the caller supplies sits between the credential and the wire: a
/// caller-supplied inner handler would see the request headers, and a returned <see cref="DelegatingHandler"/> would let
/// the caller swap its <c>InnerHandler</c>. For the same reason the credential is added to a <i>clone</i> of the request
/// and any credential header of the caller's own is overwritten. The response's <c>RequestMessage</c> is set back to the
/// caller's original, so the injected header can't be read from the response either.
/// </para>
/// <para>
/// <b>What the handler refuses,</b> by throwing <see cref="SecretTransportRefusedException"/> before anything is sent, on each
/// request (so a rotation, a deletion or a changed allowlist takes effect immediately): a request that is not https; a host not in
/// the intersection of <see cref="SecretInjection.AllowedHosts"/> and the secret's own <see cref="SecretMetadata.AllowedHosts"/>;
/// a secret that does not exist or is of the wrong kind. Creating the handler does no I/O.
/// </para>
/// <para>
/// <b>Redirects.</b> The handler never follows one, because a redirect to another host would carry the credential there;
/// a 3xx comes back to the provider unchanged, and providers must handle it themselves.
/// </para>
/// </remarks>
public interface ISecretHttpHandlerFactory
{
    /// <summary>
    /// Creates a handler that sets the credential from secret <paramref name="id"/>, as <paramref name="how"/> says, on
    /// every request. Dispose it (or the <see cref="HttpClient"/> over it) to release the connection pool.
    /// </summary>
    /// <param name="id">The secret, of kind <see cref="SecretKind.DnsApiToken"/>.</param>
    /// <param name="how">Where and how the secret goes on the request, and which hosts the caller wants it sent to.</param>
    /// <param name="transport">Timeouts, DNS refresh and proxy of the connection.</param>
    HttpMessageHandler CreateAuthenticatingHandler(SecretId id, SecretInjection how, SecretHttpTransportOptions transport);
}
