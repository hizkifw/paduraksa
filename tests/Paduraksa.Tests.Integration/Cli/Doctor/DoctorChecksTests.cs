using System.Net;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Hosting;
using Paduraksa.Core.Vault;
using Paduraksa.Server.Cli.Doctor;

namespace Paduraksa.Tests.Integration.Cli.Doctor;

/// <summary>Each of the doctor's checks over a described host (<see cref="Doctors"/>): every ok, warning and failure case.</summary>
public sealed class DoctorChecksTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<IReadOnlyList<CheckResult>> RunAsync(IDoctorCheck check, DoctorContext context) => await check.RunAsync(context, Ct);

    private static CheckResult One(IReadOnlyList<CheckResult> results, string id) => Assert.Single(results, r => r.Id == id);

    // ---- data directory

    [Fact]
    public async Task AWritableDataDirectoryWithOwnerOnlyFilesAndRoomIsOk()
    {
        FakeFiles files = new();
        files.Modes[$"{Doctors.Data}/paduraksa.db"] = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        files.Modes[$"{Doctors.Data}/vault.db"] = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        var results = await RunAsync(new DataDirectoryCheck(), Doctors.Context(files: files));

        Assert.All(results, r => Assert.Equal(CheckStatus.Ok, r.Status));
        Assert.Equal(["data-directory", "file-permissions", "disk-space"], results.Select(r => r.Id));
        Assert.Contains("paduraksa can write", One(results, "data-directory").Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingDataDirectoryWarnsThatTheFirstStartCreatesItAndNamesTheUsualMistake()
    {
        FakeFiles files = new();
        var context = Doctors.Context(files: files);
        files.Directories.Clear();

        var result = Assert.Single(await RunAsync(new DataDirectoryCheck(), context));

        Assert.Equal(CheckStatus.Warning, result.Status);
        Assert.Contains("first start creates it", result.Message, StringComparison.Ordinal);
        Assert.Contains("volume", result.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileWhereTheDataDirectoryShouldBeFails()
    {
        FakeFiles files = new();
        var context = Doctors.Context(files: files);
        files.Directories.Clear();
        files.Files.Add(Doctors.Data);

        var result = Assert.Single(await RunAsync(new DataDirectoryCheck(), context));

        Assert.Equal(CheckStatus.Failure, result.Status);
        Assert.Contains("is a file", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADataDirectoryThisUserCannotWriteFailsAndNamesTheUser()
    {
        var result = One(await RunAsync(new DataDirectoryCheck(), Doctors.Context(files: new FakeFiles { Writable = false, CurrentUser = "kitsune" })), "data-directory");

        Assert.Equal(CheckStatus.Failure, result.Status);
        Assert.Contains("kitsune", result.Message, StringComparison.Ordinal);
        Assert.Contains("10001", result.Fix, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("paduraksa.db", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead)]
    [InlineData("vault.db", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead)]
    [InlineData("vault.db-wal", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead)]
    public async Task ADatabaseFileOthersCanAccessWarnsWithItsMode(string name, UnixFileMode mode)
    {
        FakeFiles files = new();
        files.Modes[$"{Doctors.Data}/{name}"] = mode;

        var result = One(await RunAsync(new DataDirectoryCheck(), Doctors.Context(files: files)), "file-permissions");

        Assert.Equal(CheckStatus.Warning, result.Status);
        Assert.Contains($"{Doctors.Data}/{name} (mode 6", result.Message, StringComparison.Ordinal);
        Assert.Contains("chmod 600", result.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoDatabaseFilesYetIsInfoNotAWarning() =>
        Assert.Equal(CheckStatus.Info, One(await RunAsync(new DataDirectoryCheck(), Doctors.Context()), "file-permissions").Status);

    [Theory]
    [InlineData(100L << 30, 100L << 30, "Ok")]
    [InlineData(DataDirectoryCheck.WarnFreeBytes, 100L << 30, "Ok")]
    [InlineData(DataDirectoryCheck.WarnFreeBytes - 1, 100L << 30, "Warning")]
    [InlineData(DataDirectoryCheck.FailFreeBytes, 100L << 30, "Warning")]
    [InlineData(DataDirectoryCheck.FailFreeBytes - 1, 100L << 30, "Failure")]
    public async Task FreeSpaceWarnsBelowAGibibyteAndFailsBelowSixtyFourMebibytes(long free, long total, string expectedName)
    {
        var expected = Enum.Parse<CheckStatus>(expectedName);
        var result = One(await RunAsync(new DataDirectoryCheck(), Doctors.Context(files: new FakeFiles { FreeSpace = new DiskSpace(free, total) })), "disk-space");

        Assert.Equal(expected, result.Status);
        Assert.Equal(expected == CheckStatus.Ok, result.Fix is null);
    }

    [Fact]
    public async Task AnUnreadableVolumeIsInfo() =>
        Assert.Equal(CheckStatus.Info, One(await RunAsync(new DataDirectoryCheck(), Doctors.Context(files: new FakeFiles { FreeSpace = null })), "disk-space").Status);

    // ---- master key

    [Theory]
    [InlineData(KekSourceKind.GeneratedFile, "/srv/paduraksa/master.key", "master.key (generated in the data directory)")]
    [InlineData(KekSourceKind.Environment, null, "the PADURAKSA_MASTER_KEY variable")]
    [InlineData(KekSourceKind.File, "/run/secrets/master_key", "(a Docker secret)")]
    [InlineData(KekSourceKind.File, "/run/credentials/paduraksa.service/master", "(a systemd credential)")]
    [InlineData(KekSourceKind.File, "/etc/paduraksa/master.key", "(a file)")]
    public async Task TheMasterKeyIsSaidToComeFromItsSourceAndOpenTheVault(KekSourceKind source, string? file, string expected)
    {
        var results = await RunAsync(new MasterKeyCheck(), Doctors.Context(vault: Doctors.Vault(source, file)));

        var result = Assert.Single(results);
        Assert.Equal(CheckStatus.Ok, result.Status);
        Assert.Contains(expected, result.Message, StringComparison.Ordinal);
        Assert.Contains("opens vault.db", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASystemdCredentialIsRecognisedByTheCredentialsDirectoryToo()
    {
        Dictionary<string, string?> env = new() { ["CREDENTIALS_DIRECTORY"] = "/var/run/creds" };

        var result = Assert.Single(await RunAsync(new MasterKeyCheck(), Doctors.Context(vault: Doctors.Vault(KekSourceKind.File, "/var/run/creds/master"), env: env)));

        Assert.Contains("(a systemd credential)", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AKeyBesideTheVaultWarnsWithTheDashboardsWordingAndLink()
    {
        var results = await RunAsync(new MasterKeyCheck(), Doctors.Context(vault: Doctors.Vault(beside: true)));

        var warning = One(results, "master-key-location");
        Assert.Equal(CheckStatus.Warning, warning.Status);
        Assert.Contains(MasterKeyAdvice.BesideTitle, warning.Message, StringComparison.Ordinal);
        Assert.Contains(MasterKeyAdvice.BesideDetail, warning.Message, StringComparison.Ordinal);
        Assert.Contains(MasterKeyAdvice.BesideGuide, warning.Fix, StringComparison.Ordinal);
        Assert.Equal(CheckStatus.Ok, One(results, "master-key").Status);
    }

    [Fact]
    public async Task AKeyTheVaultRefusesFailsWithTheVaultsOwnSentence()
    {
        const string Refusal = "The master key file /srv/paduraksa/master.key is accessible to other users (mode 644), so the vault refuses to use it. Run: chmod 600 '/srv/paduraksa/master.key'";

        var result = Assert.Single(await RunAsync(new MasterKeyCheck(), Doctors.Context(vault: Doctors.Vault(problem: Refusal))));

        Assert.Equal(CheckStatus.Failure, result.Status);
        Assert.Equal(Refusal, result.Message);
    }

    [Fact]
    public async Task AKeyThatDoesNotOpenTheVaultFails()
    {
        var result = Assert.Single(await RunAsync(new MasterKeyCheck(), Doctors.Context(vault: Doctors.Vault(keyCheck: VaultKeyCheck.DoesNotMatch))));

        Assert.Equal(CheckStatus.Failure, result.Status);
        Assert.Contains("not the one vault.db was written under", result.Message, StringComparison.Ordinal);
        Assert.Contains("restore", result.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoKeyYetIsInfo()
    {
        var result = Assert.Single(await RunAsync(new MasterKeyCheck(), Doctors.Context(vault: Doctors.Vault(source: null, exists: false, applied: []))));

        Assert.Equal(CheckStatus.Info, result.Status);
        Assert.Contains("first start generates", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AKeyNotComparedSaysWhereItComesFromAndNothingMore()
    {
        var result = Assert.Single(await RunAsync(new MasterKeyCheck(), Doctors.Context(vault: Doctors.Vault(keyCheck: VaultKeyCheck.NotChecked))));

        Assert.Equal(CheckStatus.Ok, result.Status);
        Assert.DoesNotContain("opens", result.Message, StringComparison.Ordinal);
    }

    // ---- databases

    [Fact]
    public async Task CurrentDatabasesAreOk()
    {
        var results = await RunAsync(new DatabasesCheck(), Doctors.Context());

        Assert.Equal(["config-database", "vault-database"], results.Select(r => r.Id));
        Assert.All(results, r => Assert.Equal(CheckStatus.Ok, r.Status));
        Assert.Contains("2 migrations", results[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PendingMigrationsAreInfoBecauseTheNextStartAppliesThem()
    {
        var results = await RunAsync(new DatabasesCheck(), Doctors.Context(config: Doctors.Config(pending: ["3_C", "4_D"]), vault: Doctors.Vault(pending: ["3_V"])));

        var config = One(results, "config-database");
        Assert.Equal(CheckStatus.Info, config.Status);
        Assert.Contains("2 migrations (3_C, 4_D)", config.Message, StringComparison.Ordinal);
        Assert.Equal(CheckStatus.Info, One(results, "vault-database").Status);
        Assert.Contains("1 migration (3_V)", One(results, "vault-database").Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MigrationsThisBinaryDoesNotKnowAreADowngradeAndFail()
    {
        var results = await RunAsync(new DatabasesCheck(), Doctors.Context(config: Doctors.Config(unknown: ["9_Future"]), vault: Doctors.Vault(unknown: ["9_VaultFuture"])));

        Assert.All(results, r =>
        {
            Assert.Equal(CheckStatus.Failure, r.Status);
            Assert.Contains("newer Paduraksa", r.Message, StringComparison.Ordinal);
            Assert.Contains("downgrade", r.Message, StringComparison.Ordinal);
            Assert.Contains("restore a backup", r.Fix, StringComparison.Ordinal);
        });
        Assert.Contains("9_Future", results[0].Message, StringComparison.Ordinal);
        Assert.Contains("9_VaultFuture", results[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADatabaseThatCannotBeOpenedFailsWithTheReason()
    {
        var results = await RunAsync(new DatabasesCheck(), Doctors.Context(config: Doctors.Config(problem: "file is not a database"), vault: Doctors.Vault(databaseProblem: "unable to open database file")));

        Assert.All(results, r => Assert.Equal(CheckStatus.Failure, r.Status));
        Assert.Contains("file is not a database", results[0].Message, StringComparison.Ordinal);
        Assert.Contains("unable to open database file", results[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingDatabaseIsInfo()
    {
        var results = await RunAsync(new DatabasesCheck(), Doctors.Context(config: Doctors.Config(exists: false), vault: Doctors.Vault(exists: false)));

        Assert.All(results, r =>
        {
            Assert.Equal(CheckStatus.Info, r.Status);
            Assert.Contains("does not exist yet", r.Message, StringComparison.Ordinal);
        });
    }

    // ---- options

    [Fact]
    public async Task AValidConfigurationIsOkAndListsNothingLocked() =>
        Assert.Equal(["options"], (await RunAsync(new OptionsCheck(), Doctors.Context())).Select(r => r.Id));

    [Fact]
    public async Task AnInvalidConfigurationFailsOncePerProblem()
    {
        DoctorContext context = new() { OptionsProblems = ["Paduraksa:Listen:Http must be between 1 and 65535.", "Paduraksa:DataDirectory must not be empty."] };

        var results = await RunAsync(new OptionsCheck(), context);

        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
        {
            Assert.Equal(("options", CheckStatus.Failure), (r.Id, r.Status));
            Assert.Contains("exit code 3", r.Fix, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ASettingTheEnvironmentLocksIsListedWithItsVariable()
    {
        var context = Doctors.Context(
            configure: o => o.ServerSettingEnvironment = new Dictionary<string, string> { [ServerSettingCatalog.AdminMinPasswordLength.Key] = "14" },
            env: new Dictionary<string, string?> { ["Paduraksa__Admin__MinPasswordLength"] = "14" });

        var locked = One(await RunAsync(new OptionsCheck(), context), "setting-locked");

        Assert.Equal(CheckStatus.Info, locked.Status);
        Assert.Contains("Shortest password is 14, set by Paduraksa__Admin__MinPasswordLength", locked.Message, StringComparison.Ordinal);
        Assert.Contains("dashboard cannot change it", locked.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASettingFromAppsettingsIsNamedByItsConfigurationKey()
    {
        var context = Doctors.Context(configure: o => o.ServerSettingEnvironment = new Dictionary<string, string> { [ServerSettingCatalog.AdminMinPasswordLength.Key] = "14" });

        var locked = One(await RunAsync(new OptionsCheck(), context), "setting-locked");

        Assert.Contains("the configuration (Admin:MinPasswordLength)", locked.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AValueTheDashboardStoresThatTheEnvironmentOverridesWarnsThatItIsIgnored()
    {
        var context = Doctors.Context(
            configure: o => o.ServerSettingEnvironment = new Dictionary<string, string> { [ServerSettingCatalog.AdminMinPasswordLength.Key] = "14" },
            stored: new ServerSettings { AdminMinPasswordLength = 20 });

        var locked = One(await RunAsync(new OptionsCheck(), context), "setting-locked");

        Assert.Equal(CheckStatus.Warning, locked.Status);
        Assert.Contains("The dashboard stores 20, which is ignored", locked.Message, StringComparison.Ordinal);
        Assert.Contains("Paduraksa__Admin__MinPasswordLength", locked.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStoredValueEqualToTheEnvironmentsIsNotAWarning()
    {
        var context = Doctors.Context(
            configure: o => o.ServerSettingEnvironment = new Dictionary<string, string> { [ServerSettingCatalog.AdminMinPasswordLength.Key] = "14" },
            stored: new ServerSettings { AdminMinPasswordLength = 14 });

        Assert.Equal(CheckStatus.Info, One(await RunAsync(new OptionsCheck(), context), "setting-locked").Status);
    }

    // ---- listeners

    [Fact]
    public async Task EveryFreePortIsOkIncludingHttp3sUdp()
    {
        var results = await RunAsync(new ListenersCheck(), Doctors.Context());

        Assert.Equal(["listener-http", "listener-https", "listener-https-udp", "listener-admin"], results.Select(r => r.Id));
        Assert.All(results, r => Assert.Equal(CheckStatus.Ok, r.Status));
        Assert.Contains("public HTTP (tcp 80) is free", results[0].Message, StringComparison.Ordinal);
        Assert.Contains("HTTP/3 (udp 443)", results[2].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http3OffLeavesTheUdpPortUntestedAndTheLoopbackListenerIsAddedWhenOn()
    {
        var results = await RunAsync(new ListenersCheck(), Doctors.Context(o =>
        {
            o.Listen.Http3 = false;
            o.Listen.AdminLoopbackHttp = 9080;
        }));

        Assert.Equal(["listener-http", "listener-https", "listener-admin", "listener-admin-loopback"], results.Select(r => r.Id));
    }

    [Fact]
    public async Task APortHeldByTheRunningPaduraksaIsOkAndSaysHowItWasToldApart()
    {
        FakePorts ports = new();
        ports.States[(80, false)] = PortState.InUse;
        ports.States[(9443, false)] = PortState.InUse;
        FakeHttp http = new();
        http.Answers["healthz"] = new HttpAnswer(200, "ok\n", null, null);

        var results = await RunAsync(new ListenersCheck(), Doctors.Context(ports: ports, http: http));

        var admin = One(results, "listener-admin");
        Assert.Equal(CheckStatus.Ok, admin.Status);
        Assert.Contains("held by the running Paduraksa", admin.Message, StringComparison.Ordinal);
        Assert.Contains("https://127.0.0.1:9443/healthz", admin.Message, StringComparison.Ordinal);
        Assert.Equal(CheckStatus.Ok, One(results, "listener-http").Status);
        Assert.Equal(CheckStatus.Ok, One(results, "listener-https").Status);
    }

    [Fact]
    public async Task APortHeldBySomethingElseFailsAndSaysHowToFindIt()
    {
        FakePorts ports = new();
        ports.States[(443, false)] = PortState.InUse;
        ports.States[(443, true)] = PortState.InUse;

        var results = await RunAsync(new ListenersCheck(), Doctors.Context(ports: ports));

        var tcp = One(results, "listener-https");
        Assert.Equal(CheckStatus.Failure, tcp.Status);
        Assert.Contains("held by another process", tcp.Message, StringComparison.Ordinal);
        Assert.Contains("ss -ltnp 'sport = :443'", tcp.Fix, StringComparison.Ordinal);
        Assert.Contains("Paduraksa__Listen__Https", tcp.Fix, StringComparison.Ordinal);
        var udp = One(results, "listener-https-udp");
        Assert.Equal(CheckStatus.Failure, udp.Status);
        Assert.Contains("ss -ulnp", udp.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANonPaduraksaServiceOnTheAdminPortIsNamedAsSuch()
    {
        FakePorts ports = new();
        ports.States[(9443, false)] = PortState.InUse;
        FakeHttp http = new();
        http.Answers["healthz"] = new HttpAnswer(404, "<html>nginx</html>", null, null);

        var admin = One(await RunAsync(new ListenersCheck(), Doctors.Context(ports: ports, http: http)), "listener-admin");

        Assert.Equal(CheckStatus.Failure, admin.Status);
        Assert.Contains("answers 404, which is not Paduraksa", admin.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APrivilegedPortThisUserCannotBindWarnsUnlessAPaduraksaIsRunning()
    {
        FakePorts ports = new();
        ports.States[(80, false)] = PortState.Denied;

        var alone = One(await RunAsync(new ListenersCheck(), Doctors.Context(ports: ports)), "listener-http");
        FakeHttp http = new();
        http.Answers["healthz"] = new HttpAnswer(200, "ok\n", null, null);
        var withServer = One(await RunAsync(new ListenersCheck(), Doctors.Context(ports: ports, http: http)), "listener-http");

        Assert.Equal(CheckStatus.Warning, alone.Status);
        Assert.Contains("CAP_NET_BIND_SERVICE", alone.Message, StringComparison.Ordinal);
        Assert.Contains("docker compose exec", alone.Fix, StringComparison.Ordinal);
        Assert.Equal(CheckStatus.Info, withServer.Status);
        Assert.Contains("may be its own", withServer.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APortThatCannotBeTestedIsInfo()
    {
        FakePorts ports = new();
        ports.States[(9443, false)] = PortState.Unknown;

        Assert.Equal(CheckStatus.Info, One(await RunAsync(new ListenersCheck(), Doctors.Context(ports: ports)), "listener-admin").Status);
    }

    [Fact]
    public async Task APortOfZeroIsNotTested()
    {
        var results = await RunAsync(new ListenersCheck(), Doctors.Context(o =>
        {
            o.Listen.Http = 0;
            o.Listen.Https = 0;
            o.Listen.Http3 = false;
        }));

        Assert.Equal(["listener-admin"], results.Select(r => r.Id));
    }

    // ---- HTTP/3

    [Theory]
    [InlineData(true, true, "Ok")]
    [InlineData(true, false, "Warning")]
    [InlineData(false, false, "Info")]
    [InlineData(false, true, "Info")]
    public async Task Http3ReportsLibmsquicOnlyWhenItIsOn(bool http3, bool quic, string expectedName)
    {
        var expected = Enum.Parse<CheckStatus>(expectedName);
        var result = Assert.Single(await RunAsync(new Http3Check(), Doctors.Context(o => o.Listen.Http3 = http3, quic: quic)));

        Assert.Equal(expected, result.Status);
        if (expected == CheckStatus.Warning)
        {
            Assert.Contains("libmsquic", result.Fix, StringComparison.Ordinal);
            Assert.Contains("HTTP/3 is off", result.Message, StringComparison.Ordinal);
        }
    }

    // ---- clock

    private static readonly DateTimeOffset Noon = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private static DoctorContext ClockContext(TimeSpan skew, bool offline = false, FakeHttp? http = null)
    {
        // The local clock reads Noon+skew when the request goes out and 2 s later when the answer comes: the midpoint is the guess.
        http ??= new FakeHttp();
        http.Answers["/cdn-cgi/trace"] = new HttpAnswer(200, "fl=1", Noon + TimeSpan.FromSeconds(1), null);
        return Doctors.Context(http: http, offline: offline, time: new SteppedClock(Noon + skew, Noon + skew + TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData(0, "Ok")]
    [InlineData(25, "Ok")]
    [InlineData(-25, "Ok")]
    [InlineData(45, "Warning")]
    [InlineData(-45, "Warning")]
    [InlineData(299, "Warning")]
    [InlineData(600, "Failure")]
    [InlineData(-3600, "Failure")]
    public async Task TheClockWarnsPastThirtySecondsAndFailsPastFiveMinutes(int skewSeconds, string expectedName)
    {
        var expected = Enum.Parse<CheckStatus>(expectedName);
        var result = Assert.Single(await RunAsync(new ClockCheck(), ClockContext(TimeSpan.FromSeconds(skewSeconds))));

        Assert.Equal(expected, result.Status);
        Assert.Equal(expected is CheckStatus.Warning or CheckStatus.Failure, result.Fix is not null);
    }

    [Fact]
    public async Task TheClockSaysWhichWayItIsWrong()
    {
        var ahead = Assert.Single(await RunAsync(new ClockCheck(), ClockContext(TimeSpan.FromMinutes(10))));
        var behind = Assert.Single(await RunAsync(new ClockCheck(), ClockContext(TimeSpan.FromMinutes(-10))));

        Assert.Contains("ahead of www.cloudflare.com", ahead.Message, StringComparison.Ordinal);
        Assert.Contains("behind www.cloudflare.com", behind.Message, StringComparison.Ordinal);
        Assert.Contains("timedatectl", ahead.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheClockIsSkippedWithoutTheNetworkNeverFailed()
    {
        var unreachable = Assert.Single(await RunAsync(new ClockCheck(), Doctors.Context(http: new FakeHttp { Default = new HttpAnswer(null, null, null, "Name or service not known") })));
        FakeHttp http = new();
        http.Answers["/cdn-cgi/trace"] = new HttpAnswer(200, "fl=1", null, null);
        var noDate = Assert.Single(await RunAsync(new ClockCheck(), Doctors.Context(http: http)));

        Assert.Equal(CheckStatus.Info, unreachable.Status);
        Assert.Contains("normal without a network", unreachable.Message, StringComparison.Ordinal);
        Assert.Equal(CheckStatus.Info, noDate.Status);
    }

    [Fact]
    public async Task OfflineAsksNobody()
    {
        FakeHttp http = new();

        var result = Assert.Single(await RunAsync(new ClockCheck(), ClockContext(TimeSpan.Zero, offline: true, http: http)));

        Assert.Equal(CheckStatus.Info, result.Status);
        Assert.Contains("--offline", result.Message, StringComparison.Ordinal);
        Assert.Empty(http.Requests);
    }

    // ---- running server

    private static FakeHttp RunningServer(string version = """{"version":"0.1.0","api":"v1","commit":"abc1234"}""", int healthStatus = 200)
    {
        FakeHttp http = new();
        http.Answers["healthz"] = new HttpAnswer(healthStatus, healthStatus == 200 ? "ok\n" : "unavailable\n", null, null);
        http.Answers["/api/v1/version"] = new HttpAnswer(200, version, null, null);
        return http;
    }

    [Fact]
    public async Task NoAnswerOnTheAdminListenerIsInfo()
    {
        var result = Assert.Single(await RunAsync(new RunningServerCheck(), Doctors.Context()));

        Assert.Equal(CheckStatus.Info, result.Status);
        Assert.Contains("not running", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunningServerWithoutATokenIsOkAndItsVersionIsNotCompared()
    {
        var results = await RunAsync(new RunningServerCheck(), Doctors.Context(http: RunningServer()));

        Assert.Equal(CheckStatus.Ok, One(results, "server").Status);
        var version = One(results, "server-version");
        Assert.Equal(CheckStatus.Info, version.Status);
        Assert.Contains("API token", version.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunningServerOfTheSameBuildMatches()
    {
        FakeHttp http = RunningServer();

        var version = One(await RunAsync(new RunningServerCheck(), Doctors.Context(http: http, token: "pdk_secret")), "server-version");

        Assert.Equal(CheckStatus.Ok, version.Status);
        Assert.Contains("0.1.0 (abc1234), the same as this binary", version.Message, StringComparison.Ordinal);
        Assert.Contains(http.Requests, r => r.Uri.AbsolutePath == "/api/v1/version" && r.Token == "pdk_secret" && r.Uri.IsLoopback);
    }

    [Theory]
    [InlineData("""{"version":"0.0.9","api":"v1","commit":"abc1234"}""")]
    [InlineData("""{"version":"0.1.0","api":"v1","commit":"fff9999"}""")]
    public async Task ARunningServerOfAnotherBuildWarnsToRestartIt(string answer)
    {
        var version = One(await RunAsync(new RunningServerCheck(), Doctors.Context(http: RunningServer(answer), token: "pdk_x")), "server-version");

        Assert.Equal(CheckStatus.Warning, version.Status);
        Assert.Contains("but this binary is 0.1.0 (abc1234)", version.Message, StringComparison.Ordinal);
        Assert.Contains("restart", version.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServerThatReportsItselfUnavailableWarns()
    {
        var server = One(await RunAsync(new RunningServerCheck(), Doctors.Context(http: RunningServer(healthStatus: 503))), "server");

        Assert.Equal(CheckStatus.Warning, server.Status);
        Assert.Contains("unavailable (503)", server.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedTokenWarnsAndAnUnreadableAnswerWarns()
    {
        FakeHttp refused = RunningServer();
        refused.Answers["/api/v1/version"] = new HttpAnswer(401, "{}", null, null);
        FakeHttp garbled = RunningServer("not json");

        var one = One(await RunAsync(new RunningServerCheck(), Doctors.Context(http: refused, token: "bad")), "server-version");
        var two = One(await RunAsync(new RunningServerCheck(), Doctors.Context(http: garbled, token: "x")), "server-version");

        Assert.Equal(CheckStatus.Warning, one.Status);
        Assert.Contains("refused the version request (401)", one.Message, StringComparison.Ordinal);
        Assert.Equal(CheckStatus.Warning, two.Status);
    }

    [Fact]
    public async Task TheTokenIsNeverSentToANonLoopbackAdmin()
    {
        FakeHttp http = RunningServer();

        var version = One(await RunAsync(new RunningServerCheck(), Doctors.Context(o => o.Listen.AdminBind = "192.168.1.5", http: http, token: "pdk_secret")), "server-version");

        Assert.Equal(CheckStatus.Info, version.Status);
        Assert.DoesNotContain(http.Requests, r => r.Token is not null);
    }

    // ---- the runner

    private sealed class Crashing : IDoctorCheck
    {
        public string Name => "crashing check";

        public bool NeedsOptions => false;

        public Task<IReadOnlyList<CheckResult>> RunAsync(DoctorContext context, CancellationToken cancellationToken) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task ChecksThatNeedOptionsAreSkippedWhenTheyAreInvalidAndTheOthersStillRun()
    {
        DoctorContext context = new() { OptionsProblems = ["Paduraksa:DataDirectory must not be empty."], Offline = true };

        var report = await DoctorRunner.RunAsync(context, DoctorRunner.DefaultChecks, Ct);

        Assert.False(report.Passed);
        Assert.Equal(CheckStatus.Failure, Assert.Single(report.Checks, c => c.Id == "options").Status);
        Assert.Equal(CheckStatus.Info, Assert.Single(report.Checks, c => c.Id == "listeners").Status);
        Assert.Contains("clock", report.Checks.Select(c => c.Id));
        Assert.Equal(CheckStatus.Info, Assert.Single(report.Checks, c => c.Id == "clock").Status);
    }

    [Fact]
    public async Task ACheckThatCrashesBecomesAWarningAndTheRestRun()
    {
        var report = await DoctorRunner.RunAsync(Doctors.Context(), [new Crashing(), new Http3Check()], Ct);

        Assert.Equal(["crashing-check", "http3"], report.Checks.Select(c => c.Id));
        Assert.Equal(CheckStatus.Warning, report.Checks[0].Status);
        Assert.Contains("boom", report.Checks[0].Message, StringComparison.Ordinal);
        Assert.True(report.Passed);
    }

    [Fact]
    public void OnlyAFailureFailsTheRun()
    {
        Assert.True(new DoctorReport("0.1.0", [CheckResult.Ok("a", "x"), CheckResult.Info("b", "x"), CheckResult.Warning("c", "x", "y")]).Passed);
        Assert.False(new DoctorReport("0.1.0", [CheckResult.Ok("a", "x"), CheckResult.Failure("d", "x", "y")]).Passed);
    }

    [Fact]
    public async Task ADescribedHealthyHostPassesEveryDefaultCheck()
    {
        FakeFiles files = new();
        files.Modes[$"{Doctors.Data}/paduraksa.db"] = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var context = Doctors.Context(files: files, offline: true);

        var report = await DoctorRunner.RunAsync(context, DoctorRunner.DefaultChecks, Ct);

        Assert.True(report.Passed, string.Join("; ", report.Checks.Where(c => c.Status is CheckStatus.Failure or CheckStatus.Warning).Select(c => $"{c.Id}: {c.Message}")));
        Assert.Equal(0, report.Count(CheckStatus.Warning));
    }
}
