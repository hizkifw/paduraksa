namespace Paduraksa.Proxy.AccessLists;

/// <summary>Reads an <c>Authorization: Basic</c> value into a caller's buffer. 0 B.</summary>
internal static class BasicCredentials
{
    /// <summary>Longest <c>user:password</c> the gate accepts: a 64-byte name, the colon and a 256-byte password. Anything
    /// longer can't be a credential this build stores, so it is refused without decoding.</summary>
    public const int MaxDecodedLength = 64 + 1 + 256;

    private const string Scheme = "Basic";
    private const int MaxEncodedLength = (MaxDecodedLength + 2) / 3 * 4;

    public static BasicParse TryParse(ReadOnlySpan<char> header, Span<byte> buffer, out int length)
    {
        length = 0;
        if (header.Length <= Scheme.Length || !header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) || header[Scheme.Length] != ' ')
        {
            return BasicParse.NotBasic;
        }

        var token = header[(Scheme.Length + 1)..].Trim(' ');
        if (token.Length == 0 || token.Length > MaxEncodedLength || buffer.Length < MaxDecodedLength + 2
            || !Convert.TryFromBase64Chars(token, buffer, out length))
        {
            return BasicParse.Unreadable;
        }

        // The first colon ends the name: a password may contain colons, a name may not.
        var colon = buffer[..length].IndexOf((byte)':');
        return colon <= 0 || length > MaxDecodedLength ? BasicParse.Unreadable : BasicParse.Ok;
    }
}

internal enum BasicParse : byte
{
    Ok,

    /// <summary>A scheme other than Basic (a bearer token meant for the upstream, say).</summary>
    NotBasic,

    /// <summary>Basic, but not credentials this build could have stored: bad base64, no colon, an empty name, too long.</summary>
    Unreadable,
}
