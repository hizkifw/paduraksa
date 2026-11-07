using Paduraksa.Core.Vault;

namespace Paduraksa.Application.Certificates;

/// <summary>
/// The inventory's name rule (<see cref="CertificateNames"/>: the one the config document and the certificate table use) as the
/// vault's <see cref="ICertificateNameRules"/>, so the vault refuses an upload exactly when the inventory would drop all of its names.
/// </summary>
public sealed class CertificateNameRules : ICertificateNameRules
{
    /// <inheritdoc />
    public string? Normalize(string name) => CertificateNames.Normalize(name);
}
