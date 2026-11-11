using System.Net;
using System.Text;

using CsCheck;

using Paduraksa.Core.Model;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.AccessLists.AccessListFixtures;

namespace Paduraksa.Tests.Proxy.AccessLists;

/// <summary>
/// An access list through the real gate against the table written out plainly: a denied address or country is refused whatever is
/// sent; otherwise the visitor is allowed when the address is in a non-empty allow list and the country in a non-empty allowed
/// countries list; <c>all</c> asks an allowed visitor for the password and refuses the rest, <c>any</c> lets the allowed through
/// and asks the rest for it.
/// </summary>
public sealed class AccessListPropertyTests
{
    private static readonly string[] Networks = ["10.0.0.0/8", "10.1.0.0/16", "192.168.1.0/24", "2001:db8::/32", "203.0.113.7", "::1", "198.51.100.0/25", "10.1.2.3"];

    private static readonly string[] Clients = ["10.1.2.3", "10.9.9.9", "192.168.1.5", "203.0.113.7", "2001:db8::5", "::ffff:10.1.2.3", "198.51.100.1", "198.51.100.200", "::1", "2001:dba::1"];

    private static readonly string[] CountryCodes = ["US", "DE", "ID", "XX", "T1"];

    private static readonly Gen<ListModel> Lists = Gen.Select(
        Property.Pick(Networks).List[0, 3],
        Property.Pick(Networks).List[0, 2],
        Property.Pick(CountryCodes).List[0, 2],
        Property.Pick(CountryCodes).List[0, 2],
        Gen.Bool,
        Gen.Bool,
        Gen.Bool).Select(static (allow, deny, allowCountries, denyCountries, drop, any, users) => new ListModel(
        [.. allow.Distinct()],
        [.. deny.Distinct()],
        [.. allowCountries.Distinct()],
        [.. denyCountries.Distinct()],
        drop,
        any ? AccessListSatisfy.Any : AccessListSatisfy.All,
        users));

    // A header that isn't there, a country no one assigns, a lower-case one and a pseudo-code all have to mean what Cloudflare says.
    private static readonly Gen<(string Client, string? Country, Credentials Credentials)> Requests = Gen.Select(
        Property.Pick(Clients),
        Property.Pick("US", "DE", "id", "XX", "T1", "ZZ", null, "garbage"),
        Gen.Frequency<Credentials>([(4, Gen.Const(Credentials.None)), (3, Gen.Const(Credentials.Good)), (1, Gen.Const(Credentials.WrongPassword)), (1, Gen.Const(Credentials.UnknownUser)), (1, Gen.Const(Credentials.Unreadable))]))
        .Select(static (client, country, credentials) => (client, country, credentials));

    [Fact]
    public void TheGateAnswersWhatTheTableSays()
    {
        var runtime = AccessListFixtures.NewRuntime();
        Gen.Select(Lists, Requests.List[1, 6]).Check(
            120,
            t =>
            {
                var (model, requests) = t;
                var spec = List(
                    "prop",
                    allow: [.. model.Allow],
                    deny: [.. model.Deny],
                    drop: model.Drop,
                    satisfy: model.Satisfy,
                    users: model.Users,
                    allowCountries: [.. model.AllowCountries],
                    denyCountries: [.. model.DenyCountries]);
                var gate = GateFor(runtime, Model(spec, cloudflare: true), out _);
                foreach (var (client, country, credentials) in requests)
                {
                    var actual = RunAsync(gate, ViaCloudflare(client, country, Authorization(credentials))).GetAwaiter().GetResult();
                    var expected = model.Expected(client, country, credentials);

                    Assert.True(
                        expected == actual,
                        $"{client} from {country ?? "no header"} with {credentials}: the gate says {actual}, the table {expected}, for allow [{string.Join(",", model.Allow)}] deny [{string.Join(",", model.Deny)}] "
                        + $"allowCountries [{string.Join(",", model.AllowCountries)}] denyCountries [{string.Join(",", model.DenyCountries)}] drop {model.Drop} satisfy {model.Satisfy} users {model.Users}");
                }
            });
    }

    private static string? Authorization(Credentials credentials) => credentials switch
    {
        Credentials.Good => Basic("alice", Password),
        Credentials.WrongPassword => Basic("alice", "wrong"),
        Credentials.UnknownUser => Basic("mallory", Password),
        Credentials.Unreadable => "Basic ***",
        _ => null,
    };

    private enum Credentials
    {
        None,
        Good,
        WrongPassword,
        UnknownUser,
        Unreadable,
    }

    private sealed record ListModel(string[] Allow, string[] Deny, string[] AllowCountries, string[] DenyCountries, bool Drop, AccessListSatisfy Satisfy, bool Users)
    {
        public string Expected(string client, string? countryHeader, Credentials credentials)
        {
            var address = IPAddress.Parse(client);
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            var country = CountryOf(countryHeader);
            if (Deny.Any(n => Contains(n, address)) || DenyCountries.Contains(country))
            {
                return Drop ? "drop" : "403";
            }

            var restricts = Allow.Length > 0 || AllowCountries.Length > 0;
            var allowed = restricts && (Allow.Length == 0 || Allow.Any(n => Contains(n, address))) && (AllowCountries.Length == 0 || AllowCountries.Contains(country));
            if (restricts && !allowed && !(Users && Satisfy == AccessListSatisfy.Any))
            {
                return Drop ? "drop" : "403";
            }

            if (!Users || (allowed && Satisfy == AccessListSatisfy.Any))
            {
                return "pass";
            }

            return credentials == Credentials.Good ? "pass" : "401";
        }

        private static string CountryOf(string? header) => header switch
        {
            null => "XX",
            _ when header.Equals("T1", StringComparison.OrdinalIgnoreCase) => "T1",
            _ when header.Length == 2 && header.All(char.IsAsciiLetter) => header.ToUpperInvariant(),
            _ => "XX",
        };

        private static bool Contains(string network, IPAddress address) => network.Contains('/', StringComparison.Ordinal)
            ? IPNetwork.Parse(network).Contains(address)
            : IPAddress.Parse(network).Equals(address);
    }
}
