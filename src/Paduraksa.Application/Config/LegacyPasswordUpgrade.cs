using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Paduraksa.Core.Model;

namespace Paduraksa.Application.Config;

/// <summary>A password verifier an earlier build kept in the document, for the vault to import under the ID the document now names.</summary>
internal sealed record LegacyCredential(PasswordCredentialId Id, string Verifier)
{
    // A verifier is the one thing nothing may print.
    public override string ToString() => $"LegacyCredential {{ Id = {Id} }}";
}

/// <summary>What reading a stored document had to convert.</summary>
/// <param name="AllowLists">The inline <c>ipAllowList</c> of settings became access lists.</param>
/// <param name="Passwords">Users' <c>passwordHash</c> properties became credential IDs.</param>
/// <param name="Credentials">The verifiers those users held (fewer than users when a hash was empty), for the vault to import.</param>
internal sealed record DocumentUpgrade(bool AllowLists, bool Passwords, IReadOnlyList<LegacyCredential> Credentials)
{
    public static DocumentUpgrade None { get; } = new(false, false, []);

    /// <summary>Anything was converted, so the stored document is not what the current build would write.</summary>
    public bool Upgraded => AllowLists || Passwords;
}

/// <summary>
/// Reads the <c>passwordHash</c> an access-list user carried before passwords moved into the vault, and turns it into the
/// one way a document says it now: a <c>credentialId</c>. The verifier itself doesn't stay in the document, so the caller imports
/// the returned credentials into the vault (as they are, no re-hash) wherever it makes the document live; a document that is only
/// read (a revision on screen, a diff) needs no import.
/// </summary>
/// <remarks>
/// <b>Stable.</b> The ID is taken from the list, the user name and the verifier, so converting the same document twice gives the
/// same document and the same credentials, in every process. A user that already has a <c>credentialId</c> keeps it and its
/// verifier is imported under it.
/// </remarks>
internal static class LegacyPasswordUpgrade
{
    private const string Property = "passwordHash";

    /// <summary>The upgraded JSON and the verifiers it held, or null when no user has a <c>passwordHash</c>.</summary>
    public static (byte[] Json, IReadOnlyList<LegacyCredential> Credentials)? Upgrade(ReadOnlySpan<byte> utf8Json)
    {
        // The property name is quoted in the bytes whatever the user holds: no scan of the tree for a current document.
        if (utf8Json.IndexOf("\"passwordHash\""u8) < 0 || JsonNode.Parse(utf8Json) is not JsonObject root || root["accessLists"] is not JsonArray lists)
        {
            return null;
        }

        List<LegacyCredential> credentials = [];
        var changed = false;
        foreach (var list in lists.OfType<JsonObject>())
        {
            if (list["basicAuth"] is not JsonObject { } auth || auth["users"] is not JsonArray users)
            {
                continue;
            }

            var listId = list["id"]?.GetValue<string>() ?? string.Empty;
            foreach (var user in users.OfType<JsonObject>())
            {
                if (!user.Remove(Property, out var hash))
                {
                    continue;
                }

                changed = true;
                if (hash?.GetValue<string>() is not { Length: > 0 } verifier)
                {
                    continue;
                }

                var name = user["username"]?.GetValue<string>() ?? string.Empty;
                var id = user["credentialId"]?.GetValue<string>() is { Length: > 0 } given && PasswordCredentialId.TryParse(given.AsSpan(), out var parsed)
                    ? parsed
                    : IdOf(listId, name, verifier);
                user["credentialId"] = id.ToString();
                credentials.Add(new LegacyCredential(id, verifier));
            }
        }

        return changed ? (Encoding.UTF8.GetBytes(root.ToJsonString()), credentials) : null;
    }

    // A UUID taken from the content's hash (version 8, the vendor-defined one), like the access-list upgrade's.
    private static PasswordCredentialId IdOf(string listId, string username, string verifier)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"paduraksa:password-credential-upgrade:v1\n{listId}\n{username}\n{verifier}"));
        Span<byte> bytes = hash.AsSpan(0, 16);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return PasswordCredentialId.FromValue(new Guid(bytes, bigEndian: true));
    }
}
