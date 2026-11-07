using Microsoft.Extensions.Logging;
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
        Logs = new ListLogger();
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

    public ListLogger Logs { get; }

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

    /// <summary>Captures log messages so tests can assert nothing secret reached them.</summary>
    public sealed class ListLogger : ILogger<AcmeClient>
    {
        private readonly Lock _gate = new();
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_gate)
                {
                    return [.. _entries.Select(static e => e.Message)];
                }
            }
        }

        public IReadOnlyList<string> At(LogLevel level)
        {
            lock (_gate)
            {
                return [.. _entries.Where(e => e.Level == level).Select(static e => e.Message)];
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
