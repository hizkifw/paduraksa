using Microsoft.Extensions.DependencyInjection;
using Paduraksa.Core.Vault;
using Paduraksa.Vault.Certificates;
using Paduraksa.Vault.Crypto;
using Paduraksa.Vault.DataProtection;
using Paduraksa.Vault.Keys;
using Paduraksa.Vault.Passwords;
using Paduraksa.Vault.Persistence;
using Paduraksa.Vault.Secrets;
using Paduraksa.Vault.Totp;

namespace Paduraksa.Vault;

/// <summary>
/// The vault's only public type: registers its capabilities and nothing else. Every capability is a Core
/// interface; every implementation is <c>internal</c>.
/// </summary>
public static class VaultServiceCollectionExtensions
{
    /// <summary>
    /// Registers the vault's capabilities, its KEK rotation and its lifecycle. The vault keeps <c>vault.db</c> (and, if no
    /// other source is configured, <c>master.key</c>) in <paramref name="dataDirectory"/>, and finds its key-encryption key
    /// from <c>PADURAKSA_MASTER_KEY_FILE</c>, <c>PADURAKSA_MASTER_KEY</c> or that generated file.
    /// </summary>
    /// <remarks>
    /// Nothing is read or created here. Call <see cref="IVaultLifecycle.InitializeAsync"/> at startup so that a missing or
    /// wrong key stops the process then; a capability used first runs it anyway.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="dataDirectory">The data directory (the same one as the config database's).</param>
    /// <param name="nameRules">
    /// Which SAN names of an uploaded certificate the certificate inventory can use; the vault refuses an upload with none. Pass the
    /// inventory's own rule so the two can't disagree; null uses the vault's strict rule.
    /// </param>
    public static IServiceCollection AddPaduraksaVault(this IServiceCollection services, string dataDirectory, ICertificateNameRules? nameRules = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        return AddVault(services, VaultOptions.For(dataDirectory), nameRules);
    }

    /// <summary>The registration over explicit <see cref="VaultOptions"/>: what the vault's tests substitute (environment, clock, transport).</summary>
    internal static IServiceCollection AddVault(IServiceCollection services, VaultOptions options, ICertificateNameRules? nameRules = null)
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
        services.AddSingleton<INotificationSecretSource, NotificationSecretSource>();
        services.AddSingleton<IJwsSigner, JwsSigner>();

        services.AddSingleton(nameRules ?? StrictNameRules.Instance);
        services.AddSingleton<CertificateStore>();
        services.AddSingleton<CertificateContexts>();
        services.AddSingleton<IServerCertificateSource>(p => p.GetRequiredService<CertificateContexts>());
        services.AddSingleton<IClientCertificateSource>(p => p.GetRequiredService<CertificateContexts>());
        services.AddSingleton<ICertificateIssuance, CertificateIssuance>();
        services.AddSingleton<ICertificateImport, CertificateImport>();
        services.AddSingleton<ICertificateCatalog, CertificateCatalog>();
        services.AddSingleton<ITotpAuthenticator, TotpAuthenticator>();
        services.AddSingleton<IDataProtectionKeyStore, DataProtectionKeyStore>();
        services.AddSingleton(p => new PasswordRegistry(p.GetRequiredService<VaultOptions>().Time));
        services.AddSingleton<IPasswordVerifier>(p => p.GetRequiredService<PasswordRegistry>());
        services.AddSingleton<IPasswordCredentials, PasswordCredentialStore>();
        return services;
    }
}
