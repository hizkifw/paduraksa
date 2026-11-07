using System.Buffers;
using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Paduraksa.Core.Vault;

namespace Paduraksa.Acme;

/// <summary>
/// The parts of a JWS that ACME builds (RFC 8555 §6.2): the protected header and the external-account-binding envelope.
/// Signing the account-key JWS is the vault's job (<see cref="IJwsSigner"/>); nothing here touches a key.
/// </summary>
internal static class AcmeJws
{
    /// <summary>
    /// The protected header: <c>alg</c>, then <c>jwk</c> (<paramref name="kid"/> is <see langword="null"/>: <c>newAccount</c>)
    /// or <c>kid</c> (everything else), then <c>nonce</c> and <c>url</c>. Both are mandatory (RFC 8555 §6.4, §6.5): the
    /// signature binds the request to this URL (written exactly as it is requested), which is why a signed request cannot be replayed at another one.
    /// </summary>
    public static byte[] ProtectedHeader(JwsAlgorithm algorithm, JsonWebKeyPublic? jwk, Uri? kid, string nonce, Uri url)
    {
        if ((jwk is null) == (kid is null))
        {
            throw new ArgumentException("A protected header carries exactly one of jwk and kid (RFC 8555 §6.2).");
        }

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("alg", algorithm.ToName());
            if (jwk is not null)
            {
                writer.WritePropertyName("jwk");
                WriteJwk(writer, jwk);
            }
            else
            {
                writer.WriteString("kid", kid!.OriginalString);
            }

            writer.WriteString("nonce", nonce);
            writer.WriteString("url", url.OriginalString);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>The JWK as compact JSON (public members only; the type has no others), the payload of external account binding.</summary>
    public static byte[] JwkJson(JsonWebKeyPublic jwk)
    {
        var buffer = new ArrayBufferWriter<byte>(160);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteJwk(writer, jwk);
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// The inner JWS of external account binding (RFC 8555 §7.3.4): protected <c>{alg, kid, url}</c> (no nonce), the account
    /// JWK as payload, MACed by the CA-issued key. The vault computes the MAC; ACME assembles the envelope.
    /// </summary>
    public static JwsFlattened ExternalAccountBinding(IMacKey key, string keyId, Uri newAccountUrl, JsonWebKeyPublic accountJwk)
    {
        var buffer = new ArrayBufferWriter<byte>(160);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("alg", key.Algorithm switch
            {
                MacAlgorithm.HmacSha256 => "HS256",
                MacAlgorithm.HmacSha384 => "HS384",
                MacAlgorithm.HmacSha512 => "HS512",
                _ => throw new NotSupportedException($"{key.Algorithm} is not a JWS MAC algorithm."),
            });
            writer.WriteString("kid", keyId);
            writer.WriteString("url", newAccountUrl.OriginalString);
            writer.WriteEndObject();
        }

        var protectedPart = Base64Url.EncodeToString(buffer.WrittenSpan);
        var payloadPart = Base64Url.EncodeToString(JwkJson(accountJwk));
        var tag = new byte[key.TagLength];
        var written = key.Sign(Encoding.ASCII.GetBytes($"{protectedPart}.{payloadPart}"), tag);
        return new JwsFlattened(protectedPart, payloadPart, Base64Url.EncodeToString(tag.AsSpan(0, written)));
    }

    private static void WriteJwk(Utf8JsonWriter writer, JsonWebKeyPublic jwk)
    {
        writer.WriteStartObject();
        writer.WriteString("kty", jwk.Kty);
        WriteIfPresent(writer, "crv", jwk.Crv);
        WriteIfPresent(writer, "x", jwk.X);
        WriteIfPresent(writer, "y", jwk.Y);
        WriteIfPresent(writer, "n", jwk.N);
        WriteIfPresent(writer, "e", jwk.E);
        writer.WriteEndObject();
    }

    private static void WriteIfPresent(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }
}
