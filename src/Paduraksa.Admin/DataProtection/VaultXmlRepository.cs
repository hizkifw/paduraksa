using System.Xml.Linq;

using Microsoft.AspNetCore.DataProtection.Repositories;

using Paduraksa.Core.Vault;

namespace Paduraksa.Admin.DataProtection;

/// <summary>
/// The <c>IXmlRepository</c> over the vault's Data Protection key ring: the adapter that hands key elements to ASP.NET Core
/// Data Protection in-process and takes new ones back. It is the one reader of <see cref="IDataProtectionKeyStore"/>, and it
/// exists only in the admin app's container.
/// </summary>
/// <remarks>
/// <b>Why no <c>IXmlEncryptor</c>.</b> The vault already encrypts each element at rest under the KEK, so adding Data
/// Protection's own XML encryptor would wrap ciphertext in ciphertext and need a second key ring to protect the first; its
/// absence is deliberate. <b>Synchronous by contract:</b> Data Protection calls the repository when it loads or refreshes the
/// key ring (on the first protector use, not once per request), so a read or write is bounded by the vault's gate and never
/// part of the proxy's request path. The vault reads and stores without a <see cref="SynchronizationContext"/>, so there is
/// no deadlock risk.
/// </remarks>
internal sealed class VaultXmlRepository(IDataProtectionKeyStore keyStore) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var keys = keyStore.ReadAllAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        var elements = new List<XElement>(keys.Count);
        foreach (var key in keys)
        {
            elements.Add(XElement.Parse(key.KeyXml, LoadOptions.PreserveWhitespace));
        }

        return elements;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        // DisableFormatting keeps Data Protection's own layout: the element is written back as it was read.
        keyStore.StoreAsync(friendlyName, element.ToString(SaveOptions.DisableFormatting), CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
    }
}
