using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Core.Vault;

namespace Paduraksa.Admin.DataProtection;

/// <summary>
/// Registers ASP.NET Core Data Protection for the admin app (<c>paduraksa-admin</c>) with its key ring in the vault. The
/// dashboard (Blazor Server) needs it for antiforgery tokens and circuit state; the public proxy uses an in-memory ephemeral
/// provider instead (YARP only protects a session-affinity destination ID), and the core host resolves none.
/// </summary>
/// <remarks>
/// <b>One repository, and it is the vault's.</b> Setting <see cref="KeyManagementOptions.XmlRepository"/> to
/// <see cref="VaultXmlRepository"/> is the only way Data Protection reads or writes a key, so its default
/// <c>FileSystemXmlRepository</c> is never created and no key XML lands under <c>~/.aspnet/DataProtection-Keys</c> or
/// anywhere else on disk. <b>No <c>IXmlEncryptor</c> is added:</b> the vault already seals every element under the KEK, so a
/// second layer would only wrap ciphertext in ciphertext and need a key ring of its own to protect this one.
/// </remarks>
public static class PaduraksaDataProtection
{
    /// <summary>The admin app's Data Protection application name. It is part of the purpose strings of protected payloads, so it must not change.</summary>
    public const string ApplicationName = "paduraksa-admin";

    /// <summary>Adds Data Protection with <paramref name="keyStore"/> as its only key repository, under <see cref="ApplicationName"/>.</summary>
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
