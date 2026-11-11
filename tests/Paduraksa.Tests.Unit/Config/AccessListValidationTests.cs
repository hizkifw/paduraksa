using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

using static Paduraksa.Application.Config.ConfigErrorCodes;
using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Config;

/// <summary>An access list's own rules (addresses, realm, users and their credentials) and the references to it. A document that
/// validates can always be compiled: the addresses parse, every user names a credential of their own, and no password is in it.</summary>
public sealed class AccessListValidationTests
{
    private const string Proxy = "/hosts/0";
    private const string ListPath = "/accessLists/0";

    [Fact]
    public void TheSampleListAndAFullOneAreValid()
    {
        Assert.Empty(ConfigValidator.Validate(Valid()));
        Assert.Empty(ConfigValidator.Validate(Valid() with
        {
            AccessLists =
            [
                new AccessListConfig(
                    LanList,
                    "lan",
                    Ip: new AccessListIp(["10.0.0.0/8", "2001:db8::/32"], ["10.9.0.0/16"], DropDenied: true),
                    BasicAuth: new AccessListBasicAuth("Staff area", [new AccessListUserConfig("alice", Credential(1)), new AccessListUserConfig("Alice", Credential(2))]),
                    Satisfy: AccessListSatisfy.Any),
            ],
        }));
    }

    [Fact]
    public void AnEmptyListIsValidAndSoIsOneWithOnlyUsers()
    {
        Assert.Empty(ConfigValidator.Validate(Valid() with { AccessLists = [new AccessListConfig(LanList, "empty")] }));
        Assert.Empty(ConfigValidator.Validate(Valid() with { AccessLists = [new AccessListConfig(LanList, "passwords", BasicAuth: new AccessListBasicAuth(Users: [new AccessListUserConfig("alice", Credential(3))]))] }));
    }

    [Fact]
    public void AddressesAreCheckedWhereTheyAreWrittenAndNamedByPosition()
    {
        AssertErrors(
            Valid() with { AccessLists = [new AccessListConfig(LanList, "lan", Ip: new AccessListIp(["10.0.0.0/8", "10.0.0.0/33", "nope"], ["::1", "10.0.0.1/8"]))] },
            ($"{ListPath}/ip/allow/1", AddressInvalid),
            ($"{ListPath}/ip/allow/2", AddressInvalid),
            ($"{ListPath}/ip/deny/1", AddressInvalid));
    }

    [Fact]
    public void DroppingNeedsSomethingToRefuse()
    {
        AssertErrors(
            Valid() with { AccessLists = [new AccessListConfig(LanList, "lan", Ip: new AccessListIp(DropDenied: true))] },
            ($"{ListPath}/ip/dropDenied", AccessListDropDeniedWithoutAddresses));
        Assert.Empty(ConfigValidator.Validate(Valid() with { AccessLists = [new AccessListConfig(LanList, "lan", Ip: new AccessListIp(Deny: ["10.0.0.1"], DropDenied: true))] }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ANameIsRequired(string name) =>
        AssertErrors(Valid() with { AccessLists = [new AccessListConfig(LanList, name)] }, ($"{ListPath}/name", NameRequired));

    [Fact]
    public void NamesAreUniqueIgnoringCaseAndIdsAreToo()
    {
        AssertErrors(
            Valid() with { AccessLists = [new AccessListConfig(LanList, "Lan"), new AccessListConfig(Id<AccessListId>(2), "lan")] },
            ("/accessLists/1/name", NameDuplicate));
        AssertErrors(
            Valid() with { AccessLists = [new AccessListConfig(LanList, "one"), new AccessListConfig(LanList, "two")] },
            ("/accessLists/1/id", IdDuplicate));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("café")]
    [InlineData("line\nbreak")]
    public void ARealmMustBePrintableAscii(string realm) =>
        AssertErrors(
            Valid() with { AccessLists = [new AccessListConfig(LanList, "lan", BasicAuth: new AccessListBasicAuth(realm))] },
            ($"{ListPath}/basicAuth/realm", AccessListRealmInvalid));

    [Fact]
    public void ARealmThatIsTooLongIsRefused()
    {
        AssertErrors(
            Valid() with { AccessLists = [new AccessListConfig(LanList, "lan", BasicAuth: new AccessListBasicAuth(new string('r', AccessListLimits.MaxRealmLength + 1)))] },
            ($"{ListPath}/basicAuth/realm", AccessListRealmInvalid));
        Assert.Empty(ConfigValidator.Validate(Valid() with { AccessLists = [new AccessListConfig(LanList, "lan", BasicAuth: new AccessListBasicAuth(new string('r', AccessListLimits.MaxRealmLength)))] }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a:b")]
    [InlineData("two words")]
    [InlineData("café")]
    public void AUsernameIsVisibleAsciiWithoutAColon(string username) =>
        AssertErrors(
            WithUsers(new AccessListUserConfig(username, Credential(4))),
            ($"{ListPath}/basicAuth/users/0/username", AccessListUsernameInvalid));

    [Fact]
    public void AUsernameIsAtMostSixtyFourCharacters()
    {
        AssertErrors(
            WithUsers(new AccessListUserConfig(new string('u', AccessListLimits.MaxUsernameLength + 1), Credential(5))),
            ($"{ListPath}/basicAuth/users/0/username", AccessListUsernameInvalid));
        Assert.Empty(ConfigValidator.Validate(WithUsers(new AccessListUserConfig(new string('u', AccessListLimits.MaxUsernameLength), Credential(6)))));
    }

    [Fact]
    public void UsernamesAreCaseSensitiveAndUniqueWithinTheList()
    {
        Assert.Empty(ConfigValidator.Validate(WithUsers(new AccessListUserConfig("alice", Credential(7)), new AccessListUserConfig("ALICE", Credential(8)))));
        AssertErrors(
            WithUsers(new AccessListUserConfig("alice", Credential(9)), new AccessListUserConfig("bob", Credential(10)), new AccessListUserConfig("alice", Credential(11))),
            ($"{ListPath}/basicAuth/users/2/username", AccessListUsernameDuplicate));
    }

    [Fact]
    public void AListHasABoundedNumberOfUsers()
    {
        var users = Enumerable.Range(0, AccessListLimits.MaxUsers + 1).Select(i => new AccessListUserConfig($"user{i}", Credential(i))).ToArray();

        AssertErrors(WithUsers(users), ($"{ListPath}/basicAuth/users", AccessListUsersTooMany));
    }

    [Fact]
    public void AUserNeedsAPasswordOrTheCredentialOfOne() =>
        AssertErrors(
            WithUsers(new AccessListUserConfig("alice"), new AccessListUserConfig("bob", PasswordCredentialId.Empty)),
            ($"{ListPath}/basicAuth/users/0/password", AccessListPasswordMissing),
            ($"{ListPath}/basicAuth/users/1/password", AccessListPasswordMissing));

    [Fact]
    public void EveryUserHasACredentialOfTheirOwnAcrossTheWholeDocument()
    {
        var shared = Credential(900);
        AssertErrors(
            WithUsers(new AccessListUserConfig("alice", shared), new AccessListUserConfig("bob", shared)),
            ($"{ListPath}/basicAuth/users/1/credentialId", AccessListCredentialDuplicate));

        AccessListConfig other = new(Id<AccessListId>(2), "other", BasicAuth: new AccessListBasicAuth(Users: [new AccessListUserConfig("alice", shared)]));
        var document = WithUsers(new AccessListUserConfig("alice", shared));
        AssertErrors(
            document with { AccessLists = [.. document.AccessLists, other] },
            ("/accessLists/1/basicAuth/users/0/credentialId", AccessListCredentialDuplicate));
    }

    [Fact]
    public void APasswordThatReachesTheValidatorIsNeverAccepted()
    {
        // The server hands a password to the vault before anything is validated, so one that is still here is empty, too long, or a
        // write path that skipped the vault: none of them may be stored.
        AssertErrors(WithUsers(new AccessListUserConfig("alice", null, "")), ($"{ListPath}/basicAuth/users/0/password", AccessListPasswordEmpty));
        AssertErrors(WithUsers(new AccessListUserConfig("alice", null, new string('p', AccessListLimits.MaxPasswordBytes + 1))), ($"{ListPath}/basicAuth/users/0/password", AccessListPasswordTooLong));
        AssertErrors(WithUsers(new AccessListUserConfig("alice", Credential(13), "secret")), ($"{ListPath}/basicAuth/users/0/password", AccessListPasswordUnhashed));
    }

    [Fact]
    public void ThePasswordLimitCountsBytesNotCharacters()
    {
        // 129 two-byte characters are 258 bytes.
        AssertErrors(
            WithUsers(new AccessListUserConfig("alice", null, new string('é', AccessListLimits.MaxPasswordBytes / 2 + 1))),
            ($"{ListPath}/basicAuth/users/0/password", AccessListPasswordTooLong));
    }

    [Fact]
    public void AnUnknownHostListIsReportedWhereTheHostResolvesIt()
    {
        AccessListId missing = Id<AccessListId>(9);
        AssertErrors(
            Valid() with
            {
                Hosts = MapHost(Valid(), AppHost, h => h with { Settings = new HostSettings { AccessList = new AccessListSettings { AccessListId = missing } } }),
            },
            ($"{Proxy}/settings/accessList/accessListId", HostAccessListUnknown));
    }

    [Fact]
    public void AnUnknownRouteListIsReportedAtTheRoute()
    {
        AccessListId missing = Id<AccessListId>(9);
        AssertErrors(
            Valid() with
            {
                Hosts = MapHost(Valid(), AppHost, h => h with
                {
                    Routes = [h.Routes[0] with { Settings = new RouteSettings { AccessList = new AccessListSettings { AccessListId = missing } } }, h.Routes[1]],
                }),
            },
            ($"{Proxy}/routes/0/settings/accessList/accessListId", RouteAccessListUnknown));
    }

    [Fact]
    public void AListTheDefaultsNameIsReportedAtEachHostThatInheritsItWithTheLayer()
    {
        AccessListId missing = Id<AccessListId>(9);
        var document = Valid() with
        {
            Settings = new GlobalSettings(Defaults: new HostSettings { AccessList = new AccessListSettings { AccessListId = missing } }),
            Profiles = [],
            Hosts = [.. Valid().Hosts.Select(h => h with { ProfileId = null })],
        };

        var errors = ConfigValidator.Validate(document);

        Assert.Equal(
            [$"/hosts/0/settings/accessList/accessListId", "/hosts/1/settings/accessList/accessListId", "/hosts/2/settings/accessList/accessListId"],
            errors.Select(e => e.Path));
        Assert.All(errors, e => Assert.Contains("global defaults", e.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void ANamedListIsCheckedEvenWhileTheSwitchIsOff()
    {
        AccessListId missing = Id<AccessListId>(9);
        AssertErrors(
            Valid() with
            {
                Hosts = MapHost(Valid(), AppHost, h => h with { Settings = new HostSettings { AccessList = new AccessListSettings { Enabled = false, AccessListId = missing } } }),
            },
            ($"{Proxy}/settings/accessList/accessListId", HostAccessListUnknown));
    }

    [Fact]
    public void AValidReferenceInEveryLayerValidates()
    {
        ConfigDocument document = Valid() with
        {
            Settings = new GlobalSettings(Defaults: new HostSettings { AccessList = new AccessListSettings { AccessListId = LanList } }),
            Profiles = [new ProfileConfig(LanProfile, "LAN only", Settings: new HostSettings { AccessList = new AccessListSettings { Enabled = true } })],
            Hosts = MapHost(
                Valid(),
                AppHost,
                h => h with
                {
                    Settings = new HostSettings { AccessList = new AccessListSettings { AccessListId = LanList } },
                    Routes = [h.Routes[0] with { Settings = new RouteSettings { AccessList = new AccessListSettings { Enabled = false } } }, h.Routes[1]],
                }),
        };

        Assert.Empty(ConfigValidator.Validate(document));
    }

    private static ConfigDocument WithUsers(params AccessListUserConfig[] users) =>
        Valid() with { AccessLists = [new AccessListConfig(LanList, "lan", BasicAuth: new AccessListBasicAuth(Users: users))] };

    private static void AssertErrors(ConfigDocument document, params (string Path, string Code)[] expected) =>
        Assert.Equal(expected, ConfigValidator.Validate(document).Select(e => (e.Path, e.Code)));

    private static HostConfig[] MapHost(ConfigDocument document, HostId id, Func<HostConfig, HostConfig> change) =>
        [.. document.Hosts.Select(h => h.Id == id ? change(h) : h)];
}
