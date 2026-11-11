using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Core.Geo;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Pages;

namespace Paduraksa.Proxy.AccessLists;

/// <summary>
/// An access list as the gate uses it: the address sets compiled once, the users' password handles resolved once, and the answers
/// it gives precomputed. One instance per list and snapshot, shared by every policy that names it.
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
        CountrySet allowCountries,
        CountrySet denyCountries,
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
        AllowCountries = allowCountries;
        DenyCountries = denyCountries;
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

    /// <summary>The countries a request may come from when this isn't empty (<see cref="CountryCode.Unknown"/> is a member only if the
    /// list names <c>XX</c>).</summary>
    public CountrySet AllowCountries { get; }

    public CountrySet DenyCountries { get; }

    public bool DropDenied { get; }

    public AccessListSatisfy Satisfy { get; }

    public bool PassCredentialsUpstream { get; }

    public string Realm { get; }

    internal CompiledAccessListUser[] Users { get; }

    public bool HasUsers => Users.Length > 0;

    /// <summary>What the address side (addresses and countries) says about a request, which is where a country rule sits: a
    /// denied address or country wins; otherwise every allow list that has entries must contain the request, and a request
    /// outside one is not in the allow list. 0 B.</summary>
    internal AddressVerdict AddressVerdictOf(IpKey client, CountryCode country)
    {
        if ((!Deny.IsEmpty && Deny.Contains(client)) || (!DenyCountries.IsEmpty && DenyCountries.Contains(country)))
        {
            return AddressVerdict.Denied;
        }

        var restrictsIp = !Allow.IsEmpty;
        var restrictsCountry = !AllowCountries.IsEmpty;
        if (!restrictsIp && !restrictsCountry)
        {
            return AddressVerdict.Unrestricted;
        }

        return (!restrictsIp || Allow.Contains(client)) && (!restrictsCountry || AllowCountries.Contains(country))
            ? AddressVerdict.Allowed
            : AddressVerdict.NotInAllow;
    }

    /// <summary>The country rules' own verdict, for the trace.</summary>
    internal AddressVerdict CountryVerdictOf(CountryCode country) =>
        !DenyCountries.IsEmpty && DenyCountries.Contains(country) ? AddressVerdict.Denied
        : AllowCountries.IsEmpty ? AddressVerdict.Unrestricted
        : AllowCountries.Contains(country) ? AddressVerdict.Allowed
        : AddressVerdict.NotInAllow;

    public bool HasCountryRules => !AllowCountries.IsEmpty || !DenyCountries.IsEmpty;

    public bool HasAddressRules => !Allow.IsEmpty || !Deny.IsEmpty || !AllowCountries.IsEmpty || !DenyCountries.IsEmpty;

    public int UserCount => Users.Length;

    internal PolicyDecision Forbidden { get; }

    internal PolicyDecision OnAddressRefused { get; }

    internal PolicyDecision Unauthorized { get; }

    // A quoted-string: the realm is validated as printable ASCII, and the two characters that end or escape one are escaped.
    private static string Quote(string realm) => realm.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}

/// <summary>One user: the name as bytes for the constant-time compare, and the vault's handle on their password (which can't be
/// read, only checked against). The handle outlives a change of password, so one compiled list sees the new one.</summary>
internal sealed class CompiledAccessListUser
{
    public CompiledAccessListUser(string username, IPasswordCredential credential)
    {
        Username = username;
        Name = Encoding.ASCII.GetBytes(username);
        Credential = credential;
    }

    public string Username { get; }

    public byte[] Name { get; }

    public IPasswordCredential Credential { get; }
}
