using System.Net.Http.Headers;
using Paduraksa.Acme.Dns;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using static Paduraksa.Tests.Acme.Fakes.AcmeHarness;

namespace Paduraksa.Tests.Acme.Dns;

/// <summary>
/// Round trips against the real Cloudflare and deSEC APIs, skipped unless their environment variables are set (they create and
/// delete two TXT values at <c>_acme-challenge.&lt;name&gt;</c>, in a zone the token controls):
/// <c>PADURAKSA_LIVE_CLOUDFLARE_TOKEN</c> and <c>PADURAKSA_LIVE_CLOUDFLARE_NAME</c>; <c>PADURAKSA_LIVE_DESEC_TOKEN</c> and <c>PADURAKSA_LIVE_DESEC_NAME</c>.
/// These tests hold the token because they stand in for the vault; production code never does.
/// </summary>
public sealed class LiveDnsProviderTests
{
    public static bool CloudflareConfigured => Env("PADURAKSA_LIVE_CLOUDFLARE_TOKEN") is not null && Env("PADURAKSA_LIVE_CLOUDFLARE_NAME") is not null;

    public static bool DesecConfigured => Env("PADURAKSA_LIVE_DESEC_TOKEN") is not null && Env("PADURAKSA_LIVE_DESEC_NAME") is not null;

    [Fact(SkipUnless = nameof(CloudflareConfigured), SkipType = typeof(LiveDnsProviderTests), Skip = "Set PADURAKSA_LIVE_CLOUDFLARE_TOKEN and PADURAKSA_LIVE_CLOUDFLARE_NAME to run.")]
    public async Task CloudflareRoundTrip() =>
        await RoundTripAsync(DnsProviderKind.Cloudflare, SecretInjection.Bearer(["api.cloudflare.com"]), Env("PADURAKSA_LIVE_CLOUDFLARE_TOKEN")!, Env("PADURAKSA_LIVE_CLOUDFLARE_NAME")!);

    [Fact(SkipUnless = nameof(DesecConfigured), SkipType = typeof(LiveDnsProviderTests), Skip = "Set PADURAKSA_LIVE_DESEC_TOKEN and PADURAKSA_LIVE_DESEC_NAME to run.")]
    public async Task DesecRoundTrip() =>
        await RoundTripAsync(DnsProviderKind.DeSec, SecretInjection.Authorization("Token", ["desec.io"]), Env("PADURAKSA_LIVE_DESEC_TOKEN")!, Env("PADURAKSA_LIVE_DESEC_NAME")!);

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    private static async Task RoundTripAsync(DnsProviderKind kind, SecretInjection injection, string token, string zoneName)
    {
        var provider = new DnsProviderFactory(new LiveHandlers(token, injection)).Create(kind, SecretId.New());
        var name = "_acme-challenge." + zoneName;
        var first = "paduraksa-live-test-1";
        var second = "paduraksa-live-test-2";

        Assert.True((await provider.ValidateCredentialsAsync(Token)).Valid);
        try
        {
            await provider.CreateTxtAsync(name, first, Token);
            await provider.CreateTxtAsync(name, second, Token);
            await provider.CreateTxtAsync(name, second, Token);
        }
        finally
        {
            await provider.DeleteTxtAsync(name, first, Token);
            await provider.DeleteTxtAsync(name, second, Token);
        }
    }

    /// <summary>Stands in for the vault: adds the credential the way the vault's handler would, over a plain SocketsHttpHandler.</summary>
    private sealed class LiveHandlers(string token, SecretInjection injection) : ISecretHttpHandlerFactory
    {
        public HttpMessageHandler CreateAuthenticatingHandler(SecretId id, SecretInjection how, SecretHttpTransportOptions transport) =>
            new AddCredential(token, injection, new SocketsHttpHandler { AllowAutoRedirect = false });
    }

    private sealed class AddCredential(string token, SecretInjection injection, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Scheme != Uri.UriSchemeHttps || !injection.AllowedHosts.Contains(request.RequestUri.IdnHost))
            {
                throw new SecretTransportRefusedException("not an allowed destination");
            }

            request.Headers.Authorization = new AuthenticationHeaderValue(injection.Scheme!, token);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
