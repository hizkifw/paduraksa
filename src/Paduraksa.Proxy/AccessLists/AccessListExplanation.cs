using System.Globalization;

using Paduraksa.Core.Geo;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;

namespace Paduraksa.Proxy.AccessLists;

/// <summary>The simulator's sentence for an access-list verdict. Never echoes a password or the name a client typed.</summary>
internal static class AccessListExplanation
{
    public static string Describe(
        CompiledAccessList list, AccessListStage stage, IpKey client, CountryCode country, AccessListOutcome outcome, PolicyDecision verdict)
    {
        var address = Address(list, client, country, outcome.Address);
        var credentials = outcome.Credentials switch
        {
            CredentialVerdict.Missing => "no credentials were sent",
            CredentialVerdict.Unreadable => "the Authorization header isn't a Basic credential this proxy can read",
            CredentialVerdict.UnknownUser => "no user has that name",
            CredentialVerdict.WrongPassword => "the password is wrong",
            CredentialVerdict.NoPassword => "the user has no password in the vault, so nobody can sign in as them",
            CredentialVerdict.Verified => "the password is right",
            CredentialVerdict.VerifiedFromCache => "the password was verified a moment ago",
            CredentialVerdict.Busy => "no password check came free: the line for one was full or the wait ran out",
            CredentialVerdict.Pending => "the password is checked once a slot is free",
            CredentialVerdict.Deferred => "the password is asked after the redirect to HTTPS",
            _ => NotAsked(list, stage, outcome),
        };

        var combined = list.HasUsers && list.HasAddressRules
            ? $" (satisfy {(list.Satisfy == AccessListSatisfy.All ? "address and password" : "address or password")})"
            : string.Empty;
        var answer = verdict.Action switch
        {
            PolicyAction.Continue => "lets the request through",
            PolicyAction.Drop => "drops the connection",
            _ => $"answers {verdict.Response!.StatusCode}",
        };
        return $"access list '{list.Name}': {address}; {credentials}{combined}: {answer}";
    }

    private static string Address(CompiledAccessList list, IpKey client, CountryCode country, AddressVerdict verdict)
    {
        List<string> parts = [];
        switch (verdict)
        {
            case AddressVerdict.Denied:
                if (!list.Deny.IsEmpty && list.Deny.Contains(client))
                {
                    parts.Add(string.Create(CultureInfo.InvariantCulture, $"client {client} is denied by {Matching(list.DenyEntries, client)}"));
                }

                if (list.CountryVerdictOf(country) == AddressVerdict.Denied)
                {
                    parts.Add($"{Describe(country)} is a denied country");
                }

                break;
            case AddressVerdict.NotInAllow:
                if (!list.Allow.IsEmpty && !list.Allow.Contains(client))
                {
                    parts.Add(string.Create(CultureInfo.InvariantCulture, $"client {client} is not in the allow list ({list.AllowEntries.Length} entries)"));
                }

                if (list.CountryVerdictOf(country) == AddressVerdict.NotInAllow)
                {
                    parts.Add($"{Describe(country)} is not in the allowed countries ({list.AllowCountries.Count})");
                }

                break;
            case AddressVerdict.Allowed:
                if (!list.Allow.IsEmpty)
                {
                    parts.Add(string.Create(CultureInfo.InvariantCulture, $"client {client} is in the allow list ({Matching(list.AllowEntries, client)})"));
                }

                if (!list.AllowCountries.IsEmpty)
                {
                    parts.Add($"{Describe(country)} is an allowed country");
                }

                break;
            default:
                return "no address rules";
        }

        return string.Join(" and ", parts);
    }

    private static string Describe(CountryCode country) => $"{Countries.NameOf(country)} ({country})";

    /// <summary>The stable wire name of an address verdict, for the trace's facts.</summary>
    public static string Word(AddressVerdict verdict) => verdict switch
    {
        AddressVerdict.Denied => "denied",
        AddressVerdict.Allowed => "allowed",
        AddressVerdict.NotInAllow => "notInAllowList",
        _ => "unrestricted",
    };

    /// <summary>The stable wire name of a credential verdict, for the trace's facts. Never carries the name or the password.</summary>
    public static string Word(CredentialVerdict verdict) => verdict switch
    {
        CredentialVerdict.Missing => "missing",
        CredentialVerdict.Unreadable => "unreadable",
        CredentialVerdict.UnknownUser => "unknownUser",
        CredentialVerdict.WrongPassword => "wrongPassword",
        CredentialVerdict.NoPassword => "noPassword",
        CredentialVerdict.Verified or CredentialVerdict.VerifiedFromCache => "ok",
        CredentialVerdict.Busy => "busy",
        CredentialVerdict.Pending => "pending",
        CredentialVerdict.Deferred => "deferred",
        _ => "notAsked",
    };

    private static string NotAsked(CompiledAccessList list, AccessListStage stage, AccessListOutcome outcome) =>
        !list.HasUsers ? "the list has no users"
        : outcome.Address is AddressVerdict.Denied or AddressVerdict.NotInAllow ? "no password is asked of a refused address"
        : stage == AccessListStage.AddressOnly ? "passwords are asked on the route, after the HTTPS redirect"
        : "the address passes without a password";

    private static string Matching(IpNetwork[] entries, IpKey client)
    {
        foreach (var entry in entries)
        {
            if (entry.Contains(client))
            {
                return entry.ToString();
            }
        }

        return "an entry";
    }
}
