using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Core.Vault;

namespace Paduraksa.Admin.DataProtection;

/// <summary>
/// Registers ASP.NET Core Data Protection for the admin app (<c>paduraksa-admin</c>) with its key ring in the vault (PLAN §8.1,
/// §8.2). The dashboard (Blazor Server) needs it for antiforgery tokens and circuit state; the public proxy does <b>not</b>
/// use Data Protection, so this belongs to the admin host only.
/// </summary>
/// <remarks>
/// <para>
/// <b>One repository, and it is the vault's.</b> <see cref="KeyManagementOptions.XmlRepository"/> is set to
/// <see cref="VaultXmlRepository"/>, which is the only way Data Protection reads or writes a key: with it set,
/// Data Protection's default <c>FileSystemXmlRepository</c> is never created, so no key XML ever lands under
/// <c>~/.aspnet/DataProtection-Keys</c> or anywhere else on disk. <b>No <c>IXmlEncryptor</c> is added:</b> the vault already
/// seals every element under the KEK, so a second layer would only wrap ciphertext in ciphertext and need a key ring of its
/// own to protect this one.
/// </para>
/// <para>
/// <b>Call from the admin app only.</b> The Server composition root registers it for the admin host (<c>HostRole.Admin</c>);
/// the public and core hosts never resolve Data Protection.
/// </para>
/// </remarks>
public static class PaduraksaDataProtection
{
    /// <summary>The application name Data Protection uses. It is part of the purpose strings of protected payloads, so it must not change.</summary>
    public const string ApplicationName = "paduraksa-admin";

    /// <summary>
    /// Adds Data Protection with <paramref name="keyStore"/> as its only key repository, under the application name
    /// <see cref="ApplicationName"/>. Call once, from the admin app's service collection.
    /// </summary>
    public static IServiceCollection AddPaduraksaDataProtection(this IServiceCollection services, IDataProtectionKeyStore keyStore)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(keyStore);

        services.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .AddKeyManagementOptions(options =>
            {
                // The vault is the one repository: Data Protection's default file-system repository is never used, and the
                // vault's own envelope encryption is the only encryptor (see the class remarks).
                options.XmlRepository = new VaultXmlRepository(keyStore);
            });

        return services;
    }
}
