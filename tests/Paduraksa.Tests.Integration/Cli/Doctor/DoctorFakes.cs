using System.Net;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Vault;
using Paduraksa.Data;
using Paduraksa.Server.Cli.Doctor;
using Paduraksa.Server.Configuration;
using Paduraksa.Storage.Sqlite;

namespace Paduraksa.Tests.Integration.Cli.Doctor;

/// <summary>A host described in memory: what the doctor's checks see, without making one.</summary>
internal sealed class FakeFiles : IDoctorFileSystem
{
    public HashSet<string> Directories { get; } = [];

    public HashSet<string> Files { get; } = [];

    public Dictionary<string, UnixFileMode> Modes { get; } = [];

    public bool Writable { get; set; } = true;

    public DiskSpace? FreeSpace { get; set; } = new(50L << 30, 100L << 30);

    public string CurrentUser { get; set; } = "paduraksa";

    public bool DirectoryExists(string path) => Directories.Contains(path);

    public bool FileExists(string path) => Files.Contains(path);

    public bool CanWriteDirectory(string path) => Writable;

    public UnixFileMode? Mode(string path) => Modes.TryGetValue(path, out var mode) ? mode : null;

    public DiskSpace? Space(string path) => FreeSpace;
}

internal sealed class FakePorts : IPortProbe
{
    public Dictionary<(int Port, bool Udp), PortState> States { get; } = [];

    public PortState Probe(IPAddress address, int port, bool udp) => States.GetValueOrDefault((port, udp), PortState.Free);
}

internal sealed class FakeHttp : IDoctorHttp
{
    public Dictionary<string, HttpAnswer> Answers { get; } = new(StringComparer.Ordinal);

    public List<(Uri Uri, bool TrustAny, string? Token)> Requests { get; } = [];

    public HttpAnswer Default { get; set; } = new(null, null, null, "connection refused");

    public Task<HttpAnswer> GetAsync(Uri uri, bool trustAnyCertificate, string? bearerToken, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Requests.Add((uri, trustAnyCertificate, bearerToken));
        return Task.FromResult(Answers.GetValueOrDefault(uri.AbsolutePath == "/healthz" ? "healthz" : uri.AbsolutePath, Default));
    }
}

internal sealed class FakeQuic(bool supported) : IQuicProbe
{
    public bool IsSupported => supported;
}

/// <summary>Answers <see cref="TimeProvider.GetUtcNow"/> with each time in turn, then the last one.</summary>
internal sealed class SteppedClock(params DateTimeOffset[] times) : TimeProvider
{
    private int _next;

    public override DateTimeOffset GetUtcNow() => times[Math.Min(_next++, times.Length - 1)];
}

internal sealed class FakeVault(VaultInspection inspection) : IVaultInspection
{
    public ValueTask<VaultInspection> InspectAsync(CancellationToken cancellationToken) => new(inspection);
}

internal static class Doctors
{
    public const string Data = "/srv/paduraksa";

    public static VaultInspection Vault(
        KekSourceKind? source = KekSourceKind.GeneratedFile,
        string? file = Data + "/master.key",
        bool beside = false,
        string? problem = null,
        bool exists = true,
        string[]? applied = null,
        string[]? pending = null,
        string[]? unknown = null,
        string? databaseProblem = null,
        VaultKeyCheck keyCheck = VaultKeyCheck.Matches) =>
        new(source, file, beside, problem, exists, applied ?? ["1_A", "2_B"], pending ?? [], unknown ?? [], databaseProblem, keyCheck);

    public static ConfigDatabaseInspection Config(
        bool exists = true, string[]? applied = null, string[]? pending = null, string[]? unknown = null, string? problem = null) =>
        new(exists, new MigrationState(applied ?? ["1_A", "2_B"], pending ?? [], unknown ?? []), problem);

    public static PaduraksaOptions Options(Action<PaduraksaOptions>? configure = null)
    {
        PaduraksaOptions options = new()
        {
            DataDirectory = Data,
            Listen = new PaduraksaListenOptions { Http = 80, Https = 443, Admin = 9443, AdminBind = "0.0.0.0", Http3 = true },
        };
        configure?.Invoke(options);
        return options;
    }

    /// <summary>A healthy host: the data directory exists and is writable, the vault and database are current, every port is free, libmsquic loads.</summary>
    public static DoctorContext Context(
        Action<PaduraksaOptions>? configure = null,
        FakeFiles? files = null,
        FakePorts? ports = null,
        FakeHttp? http = null,
        VaultInspection? vault = null,
        ConfigDatabaseInspection? config = null,
        ServerSettings? stored = null,
        Dictionary<string, string?>? env = null,
        TimeProvider? time = null,
        bool quic = true,
        bool checkClock = false,
        string? token = null)
    {
        files ??= new FakeFiles();
        files.Directories.Add(Data);
        env ??= [];
        return new DoctorContext
        {
            Options = Options(configure),
            Files = files,
            Ports = ports ?? new FakePorts(),
            Http = http ?? new FakeHttp(),
            Quic = new FakeQuic(quic),
            Time = time ?? TimeProvider.System,
            Env = name => env.GetValueOrDefault(name),
            Vault = _ => new FakeVault(vault ?? Vault()),
            InspectConfigDatabase = (_, _) => Task.FromResult(config ?? Config()),
            ReadStoredSettings = (_, _) => Task.FromResult(stored),
            CheckClock = checkClock,
            ApiToken = token,
            Version = "0.1.0",
            Commit = "abc1234",
        };
    }
}
