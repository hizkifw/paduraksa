using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Core.Model;
using Paduraksa.Core.Security;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Pages;

namespace Paduraksa.Proxy.AccessLists;

/// <summary>
/// An access list as the gate uses it: the address sets compiled once, the users' hashes parsed once, and the answers it
/// gives precomputed. One instance per list and snapshot, shared by every policy that names it, so every policy of a list
/// agrees on its identity in the verification cache.
/// </summary>
public sealed class CompiledAccessList
{
    private const string Challenge = "Basic realm=\"{0}\", charset=\"UTF-8\"";

    internal CompiledAccessList(
        AccessListId id,
        string name,
        CidrSet allow,
        CidrSet deny,
        IpNetwork[] allowEntries,
        IpNetwork[] denyEntries,
        bool dropDenied,
        AccessListSatisfy satisfy,
        bool passCredentialsUpstream,
        string realm,
        CompiledAccessListUser[] users)
    {
        Id = id;
        Name = name;
        Allow = allow;
        Deny = deny;
        AllowEntries = allowEntries;
        DenyEntries = denyEntries;
        DropDenied = dropDenied;
        Satisfy = satisfy;
        PassCredentialsUpstream = passCredentialsUpstream;
        Realm = realm;
        Users = users;
        Forbidden = PolicyDecision.Respond(PrecomputedResponse.Text(StatusCodes.Status403Forbidden, "forbidden\n", htmlPage: ErrorPages.AccessDenied));
        OnAddressRefused = dropDenied ? PolicyDecision.Drop : Forbidden;
        Unauthorized = PolicyDecision.Respond(PrecomputedResponse.Text(
            StatusCodes.Status401Unauthorized,
            "authentication required\n",
            htmlPage: ErrorPages.Unauthorized,
            headers: [new PrecomputedHeader("WWW-Authenticate", new StringValues(Challenge.Replace("{0}", Quote(realm), StringComparison.Ordinal)))]));
    }

    public AccessListId Id { get; }

    public string Name { get; }

    public CidrSet Allow { get; }

    public CidrSet Deny { get; }

    /// <summary>The entries as written, for the trace: which one matched.</summary>
    internal IpNetwork[] AllowEntries { get; }

    internal IpNetwork[] DenyEntries { get; }

    public bool DropDenied { get; }

    public AccessListSatisfy Satisfy { get; }

    public bool PassCredentialsUpstream { get; }

    public string Realm { get; }

    internal CompiledAccessListUser[] Users { get; }

    public bool HasUsers => Users.Length > 0;

    internal PolicyDecision Forbidden { get; }

    internal PolicyDecision OnAddressRefused { get; }

    internal PolicyDecision Unauthorized { get; }

    // A quoted-string: the realm is validated as printable ASCII, and the two characters that end or escape one are escaped.
    private static string Quote(string realm) => realm.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}

/// <summary>One user: the name as bytes for the constant-time compare, the parsed hash, and a fingerprint of both that
/// changes when the password does (the salt is in the stored hash), so a changed password never matches a cached entry.</summary>
internal sealed class CompiledAccessListUser
{
    public CompiledAccessListUser(AccessListId list, string username, string storedHash, ParsedPasswordHash hash)
    {
        Username = username;
        Name = Encoding.ASCII.GetBytes(username);
        Hash = hash;
        Fingerprint = SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(list.ToString(), "\n", username, "\n", storedHash)))[..16];
    }

    public string Username { get; }

    public byte[] Name { get; }

    public ParsedPasswordHash Hash { get; }

    public byte[] Fingerprint { get; }
}
