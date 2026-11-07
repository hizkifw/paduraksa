using Paduraksa.Core.Vault;

namespace Paduraksa.Vault.Certificates;

/// <summary>The rule a vault built without the application's applies: the same strict rule it applies to the names of certificates it creates.</summary>
internal sealed class StrictNameRules : ICertificateNameRules
{
    public static StrictNameRules Instance { get; } = new();

    public string? Normalize(string name) => CertificateNaming.NormalizeOne(name);
}
