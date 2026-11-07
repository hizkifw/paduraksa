using Microsoft.Extensions.DependencyInjection;
using Paduraksa.Core.Vault;
using Paduraksa.Storage.Sqlite;
using Paduraksa.Vault.Crypto;
using Paduraksa.Vault.Keys;
using Paduraksa.Vault.Persistence;
using Paduraksa.Vault.Secrets;

namespace Paduraksa.Vault;

/// <summary>
/// The vault's only public type (PLAN §8): registers its capabilities and nothing else. Every capability is a Core
/// interface; every implementation is <c>internal</c>.
/// </summary>
public static class VaultServiceCollectionExtensions
{
    /// <summary>
    /// Registers the vault capabilities (<see cref="ISecretStore"/>, <see cref="IMacKeySource"/>,
    /// <see cref="ISecretHttpHandlerFactory"/>, <see cref="IJwsSigner"/>) and <see cref="IVaultLifecycle"/>. The vault keeps
    /// <c>vault.db</c> (and, if no other source is configured, <c>master.key</c>) in <paramref name="dataDirectory"/>, and finds its
    /// key-encryption key from <c>PADURAKSA_MASTER_KEY_FILE</c>, <c>PADURAKSA_MASTER_KEY</c> or that generated file (PLAN §8.3).
    /// </summary>
    /// <remarks>
    /// Nothing is read or created here. Call <see cref="IVaultLifecycle.InitializeAsync"/> at startup so that a missing or
    /// wrong key stops the process then; a capability used first runs it anyway.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="dataDirectory">The data directory (the same one as the config database's).</param>
    public static IServiceCollection AddPaduraksaVault(this IServiceCollection services, string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        return AddVault(services, VaultOptions.For(dataDirectory));
    }

    /// <summary>The registration over explicit <see cref="VaultOptions"/>: what the vault's tests substitute (environment, clock, transport).</summary>
    internal static IServiceCollection AddVault(IServiceCollection services, VaultOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddLogging();
        services.AddSingleton(options);
        services.AddVaultDbContextFactory(Path.Combine(options.DataDirectory, VaultDatabase.FileName));

        services.AddSingleton<KekProvider>();
        services.AddSingleton<VaultRuntime>();
        services.AddSingleton<IVaultLifecycle>(p => p.GetRequiredService<VaultRuntime>());
        services.AddSingleton<SecretReader>();
        services.AddSingleton<MacKeyCache>();
        services.AddSingleton<KekRotation>();
        services.AddSingleton<IVaultKekRotation>(p => p.GetRequiredService<KekRotation>());

        services.AddSingleton<ISecretStore, SecretStore>();
        services.AddSingleton<IMacKeySource, MacKeySource>();
        services.AddSingleton<ISecretHttpHandlerFactory, SecretHttpHandlerFactory>();
        services.AddSingleton<IJwsSigner, JwsSigner>();
        return services;
    }
}
