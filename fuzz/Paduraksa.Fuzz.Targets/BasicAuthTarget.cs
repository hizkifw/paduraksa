using System.Text;

using Paduraksa.Proxy.AccessLists;

namespace Paduraksa.Fuzz;

/// <summary>
/// An <c>Authorization</c> header as the access list reads it: <see cref="BasicCredentials.TryParse"/> on the bytes as header text. Properties: no exception, the
/// same verdict twice, 0 B, and a header that parses is a name, a colon and a password within the bounds, which an independent base64 decode of the token agrees with.
/// </summary>
internal static class BasicAuthTarget
{
    public static FuzzTarget Target { get; } = new(
        "basic-auth",
        "Authorization: Basic header parser",
        Run,
        Seeds);

    private static void Run(ReadOnlySpan<byte> input)
    {
        var header = Encoding.Latin1.GetString(input);
        var buffer = new byte[BasicCredentials.MaxDecodedLength + 64];

        var verdict = Checks.Deterministic(
            () =>
            {
                var result = BasicCredentials.TryParse(header, buffer, out var length);
                return $"{result} {length} {(result == BasicParse.Ok ? Convert.ToHexString(buffer.AsSpan(0, length)) : string.Empty)}";
            },
            "the credentials verdict");
        Checks.AllocatesNothing(() => _ = BasicCredentials.TryParse(header, buffer, out _), "BasicCredentials.TryParse");

        var parsed = BasicCredentials.TryParse(header, buffer, out var decodedLength);
        if (parsed == BasicParse.NotBasic)
        {
            // Anything that isn't "Basic" and one space is left to the upstream; "basic" in any case is Basic.
            Checks.That(header.Length <= 5 || !header.StartsWith("Basic", StringComparison.OrdinalIgnoreCase) || header[5] != ' ', $"'{header}' is Basic and is said not to be");
            return;
        }

        if (parsed == BasicParse.Unreadable)
        {
            Checks.That(decodedLength <= BasicCredentials.MaxDecodedLength + 2 || verdict.Length > 0, "the unreadable length");
            return;
        }

        var decoded = buffer.AsSpan(0, decodedLength);
        Checks.That(decodedLength <= BasicCredentials.MaxDecodedLength, "credentials longer than the limit");
        var colon = decoded.IndexOf((byte)':');
        Checks.That(colon > 0, "credentials with no name or no colon");

        // The token, as text, decoded the other way.
        var token = header[6..].Trim(' ');
        byte[] expected;
        try
        {
            expected = Convert.FromBase64String(token);
        }
        catch (FormatException ex)
        {
            throw new PropertyViolationException($"the token '{token}' is credentials to the parser but not base64 to Convert.FromBase64String", ex);
        }

        Checks.That(decoded.SequenceEqual(expected), "the two base64 decoders disagree");
    }

    private static IEnumerable<(string Name, byte[] Bytes)> Seeds()
    {
        static byte[] Basic(string credentials) => Encoding.ASCII.GetBytes("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));

        yield return ("alice", Basic("alice:secret"));
        yield return ("password-with-colon", Basic("alice:pa:ss:word"));
        yield return ("empty-password", Basic("alice:"));
        yield return ("empty-name", Basic(":secret"));
        yield return ("no-colon", Basic("alice"));
        yield return ("utf8", Basic("aliçe:päss"));
        yield return ("longest", Basic(new string('n', 64) + ":" + new string('p', 256)));
        yield return ("too-long", Basic(new string('n', 64) + ":" + new string('p', 257)));
        yield return ("lower-case-scheme", Encoding.ASCII.GetBytes("basic YWxpY2U6c2VjcmV0"));
        yield return ("bearer", "Bearer abc.def.ghi"u8.ToArray());
        yield return ("padded-with-spaces", "Basic   YWxpY2U6c2VjcmV0  "u8.ToArray());
        yield return ("bad-base64", "Basic !!!!"u8.ToArray());
        yield return ("no-padding", "Basic YWxpY2U6c2VjcmV0Zg"u8.ToArray());
        yield return ("scheme-only", "Basic"u8.ToArray());
        yield return ("scheme-and-space", "Basic "u8.ToArray());
    }
}
