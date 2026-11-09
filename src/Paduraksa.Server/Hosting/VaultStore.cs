using System.Net.Security;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Paduraksa.Application.Certificates;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Server.Configuration;
using Paduraksa.Vault;

namespace Paduraksa.Server.Hosting;

/// <summary>The vault capabilities the composition root hands on (the rest stays inside the vault's container).</summary>
/// <param name="Lifecycle">Brings the vault up at startup.</param>
/// <param name="ServerCertificates">The vault's own serving source (before any composition with the placeholder).</param>
/// <param name="Issuance">Key and chain creation, replacement and deletion.</param>
/// <param name="Import">Upload of a certificate with its key.</param>
/// <param name="Catalog">The vault describing the certificates it holds (public facts only).</param>
/// <param name="Signer">Signs ACME requests with the vault-held account keys.</param>
/// <param name="MacKeys">Opens external-account-binding MAC keys for the ACME client.</param>
/// <param name="Secrets">Secret metadata and storage (DNS provider credentials).</param>
/// <param name="SecretHandlers">The authenticating HTTP handlers DNS providers build their clients over.</param>
/// <param name="Totp">Two-factor codes for admin users (the secret never leaves the vault).</param>
/// <param name="KeyRing">The admin app's Data Protection key ring (the dashboard's antiforgery and circuit state).</param>
internal sealed record VaultCapabilities(
    IVaultLifecycle Lifecycle,
    IServerCertificateSource ServerCertificates,
    ICertificateIssuance Issuance,
    ICertificateImport Import,
    ICertificateCatalog Catalog,
    IJwsSigner Signer,
    IMacKeySource MacKeys,
    ISecretStore Secrets,
    ISecretHttpHandlerFactory SecretHandlers,
    ITotpAuthenticator Totp,
    IDataProtectionKeyStore KeyRing);

/// <summary>
/// Composes the vault into the shared services: the one <c>AddPaduraksaVault</c> registration, over the data
/// directory, in a container of its own that <see cref="SharedServices"/> owns (the container disposes the vault, which zeroes
/// its key-encryption key, after every host and the config store have stopped). Nothing is read or created here: the core host's
/// <see cref="VaultStartup"/> initializes it, before the config store starts.
/// </summary>
internal static class VaultStore
{
    public static VaultCapabilities AddTo(SharedServices.Builder builder, string dataDirectory, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ServiceProvider container = builder.Own(new ServiceCollection()
            .AddSingleton(loggerFactory)
            .AddPaduraksaVault(dataDirectory, new CertificateNameRules())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));

        IVaultLifecycle lifecycle = builder.AddOwnedElsewhere(container.GetRequiredService<IVaultLifecycle>());
        return new VaultCapabilities(
            lifecycle,
            container.GetRequiredService<IServerCertificateSource>(),
            container.GetRequiredService<ICertificateIssuance>(),
            container.GetRequiredService<ICertificateImport>(),
            container.GetRequiredService<ICertificateCatalog>(),
            container.GetRequiredService<IJwsSigner>(),
            container.GetRequiredService<IMacKeySource>(),
            container.GetRequiredService<ISecretStore>(),
            container.GetRequiredService<ISecretHttpHandlerFactory>(),
            container.GetRequiredService<ITotpAuthenticator>(),
            container.GetRequiredService<IDataProtectionKeyStore>());
    }
}

/// <summary>
/// The serving source of the process: the vault's certificates first, then the placeholder (only when it is enabled, see
/// <see cref="PaduraksaDevelopmentOptions.PlaceholderCertificate"/>) for the IDs the vault doesn't know. A certificate the vault
/// holds is never shadowed by the placeholder.
/// </summary>
/// <remarks>
/// <paramref name="fallbackOnlyId"/> is an ID only the fallback can know (the placeholder's well-known one): it is not looked up in the
/// vault, which would be a database read for a certain miss on every compile.
/// </remarks>
internal sealed class CompositeServerCertificateSource(IServerCertificateSource vault, IServerCertificateSource? fallback, CertificateId fallbackOnlyId = default) : IServerCertificateSource
{
    public async ValueTask<SslStreamCertificateContext?> LoadForServingAsync(CertificateId id, CancellationToken cancellationToken)
    {
        if (fallback is not null && !fallbackOnlyId.IsEmpty && id == fallbackOnlyId)
        {
            return await fallback.LoadForServingAsync(id, cancellationToken).ConfigureAwait(false);
        }

        return await vault.LoadForServingAsync(id, cancellationToken).ConfigureAwait(false)
            ?? (fallback is null ? null : await fallback.LoadForServingAsync(id, cancellationToken).ConfigureAwait(false));
    }
}
