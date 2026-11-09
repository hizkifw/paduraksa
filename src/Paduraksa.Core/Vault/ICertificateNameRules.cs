namespace Paduraksa.Core.Vault;

/// <summary>
/// Which names of an uploaded certificate the certificate inventory can use: the one rule both sides apply, so
/// the vault never accepts an upload whose names the inventory then refuses. The application supplies it (its normaliser is the
/// one the config document and the certificate table use); a vault built without one applies its own strict rule.
/// </summary>
public interface ICertificateNameRules
{
    /// <summary>The normal form of <paramref name="name"/> (a certificate SAN), or <see langword="null"/> if the inventory would drop it.</summary>
    string? Normalize(string name);
}
