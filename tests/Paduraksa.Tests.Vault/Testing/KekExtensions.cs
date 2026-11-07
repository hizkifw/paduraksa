namespace Paduraksa.Vault.Crypto;

/// <summary>
/// The text form of a KEK as a string, which production code deliberately cannot make (a string cannot be zeroed): tests
/// need it to put a key in a variable or a file. In the namespace of <see cref="Kek"/> so it is found like a member.
/// </summary>
internal static class KekExtensions
{
    public static string Encode(this Kek kek) => Convert.ToBase64String(kek.Bytes);
}
