using System.Text;

namespace Paduraksa.Core.Vault;

/// <summary>
/// One element of the ASP.NET Core Data Protection key ring as the vault holds it (PLAN §8.1, §8.2).
/// </summary>
/// <param name="FriendlyName">The name Data Protection stored the element under (ASP.NET Core's own key name). Not a secret.</param>
/// <param name="KeyXml">
/// The element as Data Protection wrote it. It contains the raw key material, so this is the <b>third sanctioned egress</b>
/// of key material out of the vault: only the admin app's <c>IXmlRepository</c> adapter may read it, and only to hand it
/// straight back to Data Protection in-process, exactly as a TLS context goes to Kestrel (PLAN §8.2). Never log it,
/// never return it from an API, never write it to disk.
/// </param>
public sealed record DataProtectionKey(string FriendlyName, string KeyXml)
{
    /// <summary>
    /// The record's own printing would include <see cref="KeyXml"/> (in <c>ToString</c>, string interpolation, a debugger
    /// display or a log scope). Only the friendly name is printed; the element is key material.
    /// </summary>
    private bool PrintMembers(StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Append("FriendlyName = ").Append(FriendlyName);
        return true;
    }
}

/// <summary>
/// The ASP.NET Core Data Protection key ring (PLAN §8.1: the dashboard's antiforgery and circuit state), held by the vault: the key elements live
/// in <c>vault.db</c> sealed under the KEK like every other key, never as plain XML files on disk.
/// </summary>
/// <remarks>
/// <para>
/// The surface is the minimum an <c>Microsoft.AspNetCore.DataProtection.Repositories.IXmlRepository</c> needs: read every
/// element, store one new element under a friendly name. Data Protection does not need to delete or update elements
/// (retired keys are simply not used any more), so the vault does not offer it.
/// </para>
/// <para>
/// <b>Why the XML leaves the vault.</b> Data Protection is a framework component that must parse the element and do the
/// cryptography itself; there is no capability-shaped seam for it. The alternative - leaving the ring as plain XML under
/// <c>/data</c> - would put raw key material on disk outside the vault, which is the thing the trust boundary exists to
/// prevent. Handing the elements to Data Protection in-process is therefore a deliberate, documented egress with one
/// consumer, not a general read API (PLAN §8.2). A sealed value opened by the vault is zeroed again after the string is
/// produced, but the string itself is the framework's to manage.
/// </para>
/// <para>
/// No <c>IXmlEncryptor</c> is involved: the vault already encrypts at rest, so Data Protection would only wrap ciphertext
/// in ciphertext (and would need a second key ring to do it).
/// </para>
/// </remarks>
public interface IDataProtectionKeyStore
{
    /// <summary>Every element of the ring, sealed values opened. An empty ring returns an empty list; this never returns null.</summary>
    ValueTask<IReadOnlyList<DataProtectionKey>> ReadAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Seals <paramref name="keyXml"/> and stores it under <paramref name="friendlyName"/>. Storing the same name again
    /// replaces that element (Data Protection uses a fresh name for every new key).
    /// </summary>
    /// <exception cref="ArgumentException">The name or the element is empty or too large.</exception>
    ValueTask StoreAsync(string friendlyName, string keyXml, CancellationToken cancellationToken);
}
