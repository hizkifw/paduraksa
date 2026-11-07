using Paduraksa.Core.Vault;

namespace Paduraksa.Vault.Secrets;

/// <summary>What each <see cref="SecretKind"/> may be used for, in one place.</summary>
internal static class SecretKinds
{
    /// <summary>The kinds that are HMAC keys (opened through <see cref="IMacKeySource"/>), which need a <see cref="MacAlgorithm"/>.</summary>
    public static readonly IReadOnlyList<SecretKind> MacKinds = [SecretKind.EabHmacKey, SecretKind.TsigKey, SecretKind.HmacSigningKey];

    public static bool IsMac(SecretKind kind) => kind is SecretKind.EabHmacKey or SecretKind.TsigKey or SecretKind.HmacSigningKey;

    /// <summary>The purpose name sealed into a secret's AAD: its stable kind name.</summary>
    public static string Purpose(SecretKind kind) => kind.ToName();
}
