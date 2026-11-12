namespace Paduraksa.Core.Vault;

/// <summary>
/// The stable, lowercase names of the vault enums, which are what <c>vault.db</c> and the config database persist (the
/// same rule as <c>ModelNames</c>): never rename one, only add. The numeric enum values are never stored.
/// </summary>
public static class VaultNames
{
    /// <summary>The stable name of <paramref name="spec"/>, e.g. <c>ecdsa-p256</c>.</summary>
    public static string ToName(this KeySpec spec) => spec switch
    {
        KeySpec.EcdsaP256 => "ecdsa-p256",
        KeySpec.EcdsaP384 => "ecdsa-p384",
        KeySpec.Rsa2048 => "rsa-2048",
        KeySpec.Rsa3072 => "rsa-3072",
        KeySpec.Rsa4096 => "rsa-4096",
        _ => throw Undefined(spec),
    };

    /// <summary>The stable name of <paramref name="algorithm"/>, e.g. <c>hmac-sha256</c>.</summary>
    public static string ToName(this MacAlgorithm algorithm) => algorithm switch
    {
        MacAlgorithm.HmacSha256 => "hmac-sha256",
        MacAlgorithm.HmacSha384 => "hmac-sha384",
        MacAlgorithm.HmacSha512 => "hmac-sha512",
        _ => throw Undefined(algorithm),
    };

    /// <summary>The stable name of <paramref name="kind"/>, e.g. <c>dns-api-token</c>.</summary>
    public static string ToName(this SecretKind kind) => kind switch
    {
        SecretKind.DnsApiToken => "dns-api-token",
        SecretKind.EabHmacKey => "eab-hmac-key",
        SecretKind.TotpSecret => "totp-secret",
        SecretKind.TsigKey => "tsig-key",
        SecretKind.HmacSigningKey => "hmac-signing-key",
        SecretKind.NotificationSecret => "notification-secret",
        _ => throw Undefined(kind),
    };

    /// <exception cref="FormatException"><paramref name="name"/> is not one of the names of <see cref="KeySpec"/>.</exception>
    public static KeySpec ParseKeySpec(string name) => name switch
    {
        "ecdsa-p256" => KeySpec.EcdsaP256,
        "ecdsa-p384" => KeySpec.EcdsaP384,
        "rsa-2048" => KeySpec.Rsa2048,
        "rsa-3072" => KeySpec.Rsa3072,
        "rsa-4096" => KeySpec.Rsa4096,
        _ => throw NotAName<KeySpec>(name),
    };

    /// <exception cref="FormatException"><paramref name="name"/> is not one of the names of <see cref="MacAlgorithm"/>.</exception>
    public static MacAlgorithm ParseMacAlgorithm(string name) => name switch
    {
        "hmac-sha256" => MacAlgorithm.HmacSha256,
        "hmac-sha384" => MacAlgorithm.HmacSha384,
        "hmac-sha512" => MacAlgorithm.HmacSha512,
        _ => throw NotAName<MacAlgorithm>(name),
    };

    /// <exception cref="FormatException"><paramref name="name"/> is not one of the names of <see cref="SecretKind"/>.</exception>
    public static SecretKind ParseSecretKind(string name) => name switch
    {
        "dns-api-token" => SecretKind.DnsApiToken,
        "eab-hmac-key" => SecretKind.EabHmacKey,
        "totp-secret" => SecretKind.TotpSecret,
        "tsig-key" => SecretKind.TsigKey,
        "hmac-signing-key" => SecretKind.HmacSigningKey,
        "notification-secret" => SecretKind.NotificationSecret,
        _ => throw NotAName<SecretKind>(name),
    };

    private static FormatException NotAName<T>(string? name)
        where T : struct, Enum =>
        new($"'{name}' is not a {typeof(T).Name} name.");

    private static ArgumentOutOfRangeException Undefined<T>(T value)
        where T : struct, Enum =>
        new(nameof(value), value, $"Undefined {typeof(T).Name}.");
}
