using Paduraksa.Acme;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Tests.Acme.Fakes;

/// <summary>A client wired to the fake CA, a fake vault and a virtual clock, with the logs it wrote.</summary>
internal sealed class AcmeHarness : IDisposable
{
    /// <summary>The options tests start from: short polling bounds, and a request deadline the virtual clock never reaches.</summary>
    public static readonly AcmeClientOptions DefaultOptions = new()
    {
        PollInterval = TimeSpan.FromSeconds(2),
        MinPollDelay = TimeSpan.FromSeconds(1),
        MaxPollDelay = TimeSpan.FromSeconds(10),
    };

    public AcmeHarness(AcmeClientOptions? options = null, FakeAcmeServer? server = null, Func<HttpMessageHandler, HttpMessageHandler>? wrap = null)
    {
        Server = server ?? new FakeAcmeServer();
        Signer = new FakeJwsSigner();
        Time = new VirtualTimeProvider();
        Logs = new ListLogger<AcmeClient>();
        EabSecretId = SecretId.New();
        EabKey = new FakeMacKey(Server.EabSecret);
        Client = new AcmeClient(
            FakeAcmeServer.Url("directory"),
            Signer,
            new FakeMacKeySource(EabSecretId, EabKey),
            (options ?? DefaultOptions) with { RequestTimeout = TimeSpan.FromDays(1) },
            wrap?.Invoke(Server) ?? Server,
            Time,
            Logs);
    }

    public FakeAcmeServer Server { get; }

    public FakeJwsSigner Signer { get; }

    public VirtualTimeProvider Time { get; }

    public ListLogger<AcmeClient> Logs { get; }

    public SecretId EabSecretId { get; }

    public FakeMacKey EabKey { get; }

    public AcmeClient Client { get; }

    public static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Creates a key for a new account ID in the fake vault and registers the account.</summary>
    public async Task<AcmeAccount> RegisterAsync(
        AcmeAccountId? id = null, AcmeNewAccountRequest? request = null, JwsAlgorithm algorithm = JwsAlgorithm.ES256)
    {
        var accountId = id ?? AcmeAccountId.New();
        if (await Signer.GetAccountKeyAsync(accountId, Token) is null)
        {
            await Signer.CreateAccountKeyAsync(accountId, algorithm, Token);
        }

        return await Client.RegisterAccountAsync(
            accountId, request ?? new AcmeNewAccountRequest(["mailto:ops@example.com"], TermsOfServiceAgreed: true), Token);
    }

    public void Dispose() => Client.Dispose();
}
