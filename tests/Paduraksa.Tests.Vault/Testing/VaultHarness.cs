using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Paduraksa.Core.Vault;
using Paduraksa.Vault;
using Paduraksa.Vault.Crypto;

namespace Paduraksa.Tests.Vault.Testing;

/// <summary>
/// A vault over a temp directory: the real registration (<c>VaultServiceCollectionExtensions.AddVault</c>) with the
/// environment, clock and transport substituted through the internal seam. <see cref="Restart"/> builds it again over the same
/// directory and variables, which is what a process restart is.
/// </summary>
internal sealed class VaultHarness : IDisposable
{
    private readonly List<ServiceProvider> _others = [];

    private ServiceProvider? _services;

    public TempDirectory Data { get; } = new();

    /// <summary>The environment the vault sees. Starts with a fresh master key in <c>PADURAKSA_MASTER_KEY</c>.</summary>
    public Dictionary<string, string?> Variables { get; } = [];

    public ManualTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));

    public VaultPolicy Policy { get; set; } = VaultPolicy.Default;

    public Func<SecretHttpTransportOptions, HttpMessageHandler>? Terminal { get; set; }

    public ListLogger<KekProvider> KekLog { get; } = new();

    public VaultHarness()
    {
        using var kek = Kek.Generate();
        Variables[KekProvider.KeyVariable] = kek.Encode();
    }

    public ServiceProvider Services => _services ??= Build();

    public T Get<T>()
        where T : notnull => Services.GetRequiredService<T>();

    /// <summary>A second vault over the same directory, key and clock: another process using the same database.</summary>
    public ServiceProvider Another()
    {
        var provider = Build();
        _others.Add(provider);
        return provider;
    }

    public VaultHarness Restart()
    {
        _services?.Dispose();
        _services = null;
        return this;
    }

    public void Dispose()
    {
        _others.ForEach(p => p.Dispose());
        _services?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Data.Dispose();
    }

    private ServiceProvider Build()
    {
        var services = new ServiceCollection();
        var options = new VaultOptions(
            Data.Path,
            name => Variables.GetValueOrDefault(name),
            Time,
            Policy,
            Terminal ?? Paduraksa.Vault.Secrets.TerminalHandlers.CreateSockets);
        VaultServiceCollectionExtensions.AddVault(services, options);
        services.AddSingleton<ILogger<KekProvider>>(KekLog);
        return services.BuildServiceProvider(validateScopes: true);
    }
}
