using System.Security.Cryptography;

namespace Paduraksa.Core;

/// <summary>
/// The one definition of what a master key (KEK) looks like as text: standard base64 of exactly 32 bytes, in its
/// canonical spelling, not all zeros. The vault parses its key with it and the CLI checks a new key with it before storing
/// anything, so the two can't disagree.
/// </summary>
public static class MasterKeyFormat
{
    /// <summary>The key's length in bytes.</summary>
    public const int KeyLength = 32;

    /// <summary>The length of the text form (base64 with padding).</summary>
    public const int TextLength = 44;

    /// <summary>Decodes <paramref name="text"/> (exactly, no surrounding whitespace) into <paramref name="key"/>, which must be <see cref="KeyLength"/> bytes. False if it isn't a key.</summary>
    public static bool TryDecode(ReadOnlySpan<char> text, Span<byte> key)
    {
        if (text.Length != TextLength || key.Length != KeyLength)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[KeyLength + 3];
        Span<char> canonical = stackalloc char[TextLength];
        try
        {
            if (!Convert.TryFromBase64Chars(text, bytes, out var written) || written != KeyLength)
            {
                return false;
            }

            // Non-canonical encodings (non-zero padding bits) and all-zero keys only a mistake produces.
            Convert.TryToBase64Chars(bytes[..KeyLength], canonical, out _);
            if (!text.SequenceEqual(canonical) || !bytes[..KeyLength].ContainsAnyExcept((byte)0))
            {
                return false;
            }

            bytes[..KeyLength].CopyTo(key);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>True if <paramref name="text"/>, ignoring surrounding whitespace, is a master key.</summary>
    public static bool IsValid(ReadOnlySpan<char> text)
    {
        Span<byte> key = stackalloc byte[KeyLength];
        try
        {
            return TryDecode(text.Trim(), key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
