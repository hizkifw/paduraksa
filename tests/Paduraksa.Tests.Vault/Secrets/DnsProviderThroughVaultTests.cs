using System.Text;
using Paduraksa.Acme.Dns;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Acme.Dns;
using Paduraksa.Tests.Vault.Testing;

namespace Paduraksa.Tests.Vault.Secrets;

/// <summary>
/// The Cloudflare provider over the vault's real authenticating handler: the secret is read and attached inside the vault, and
/// the vault's allow list, not the provider, decides where it may go. Only the terminal transport is replaced (an in-memory
/// Cloudflare instead of the network), through the vault's internal seam.
/// </summary>
public sealed class DnsProviderThroughVaultTests : IDisposable
{
    private const string Challenge = "_acme-challenge.example.com";

    private readonly VaultHarness _vault = new();
    private readonly FakeCloudflare _cloudflare = new();
    private readonly List<ApiCall> _terminalSaw = [];

    public DnsProviderThroughVaultTests()
    {
        _cloudflare.AddZone("example.com");
        _vault.Terminal = _ => new Terminal(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _vault.Dispose();

    private async Task<IDnsProvider> ProviderAsync(params string[] allowedHosts)
    {
        SecretId id = await _vault.Get<ISecretStore>().PutAsync(SecretKind.DnsApiToken, Encoding.UTF8.GetBytes(FakeSecretHandlers.Token), SecretStoreTests.Dns(allowedHosts), Ct);
        return new DnsProviderFactory(_vault.Get<ISecretHttpHandlerFactory>()).Create(DnsProviderKind.Cloudflare, id);
    }

    [Fact]
    public async Task TheVaultAttachesTheBearerTokenAndTheRecordIsWritten()
    {
        IDnsProvider provider = await ProviderAsync("api.cloudflare.com");

        await provider.CreateTxtAsync(Challenge, "v1", Ct);
        Assert.True((await provider.ValidateCredentialsAsync(Ct)).Valid);

        Assert.Equal([(Challenge, "v1")], _cloudflare.Records("example.com"));
        Assert.NotEmpty(_terminalSaw);
        Assert.All(_terminalSaw, c => Assert.Equal($"Bearer {FakeSecretHandlers.Token}", c.Authorization));
        Assert.All(_terminalSaw, c => Assert.Equal("api.cloudflare.com", c.Url.Host));
    }

    [Fact]
    public async Task ASecretThatDoesNotAllowTheApiHostIsRefusedPermanentlyWithoutTheTokenInTheMessage()
    {
        IDnsProvider provider = await ProviderAsync("dns.example.net");

        DnsProviderException ex = await Assert.ThrowsAsync<DnsProviderException>(async () => await provider.CreateTxtAsync(Challenge, "v1", Ct));

        Assert.False(ex.IsTransient);
        Assert.Contains("vault refused", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeSecretHandlers.Token, ex.ToString(), StringComparison.Ordinal);
        Assert.Empty(_terminalSaw);
    }

    [Fact]
    public async Task ASecretThatIsGoneIsRefusedPermanently()
    {
        // A real DNS token is stored, so the vault is not empty; the provider below names a secret that does not exist.
        _ = await ProviderAsync("api.cloudflare.com");
        SecretId unknown = SecretId.New();
        IDnsProvider orphan = new DnsProviderFactory(_vault.Get<ISecretHttpHandlerFactory>()).Create(DnsProviderKind.Cloudflare, unknown);

        DnsProviderException ex = await Assert.ThrowsAsync<DnsProviderException>(async () => await orphan.CreateTxtAsync(Challenge, "v1", Ct));

        Assert.False(ex.IsTransient);
        Assert.Empty(_terminalSaw);
    }

    private sealed class Terminal(DnsProviderThroughVaultTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            ApiCall call = new(request.Method, request.RequestUri!, body, request.Headers.Authorization?.ToString() ?? string.Empty);
            lock (owner._terminalSaw)
            {
                owner._terminalSaw.Add(call);
            }

            return owner._cloudflare.Handle(call);
        }
    }
}
