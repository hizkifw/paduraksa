using Microsoft.EntityFrameworkCore;
using Paduraksa.Application.Audit;
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Compilation;
using Paduraksa.Application.Config;
using Paduraksa.Application.Revisions;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Tests.Data.Config;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;
using SnapshotStore = Paduraksa.Proxy.Snapshot.SnapshotStore;

namespace Paduraksa.Tests.Data.Commands;

/// <summary>
/// A <see cref="ConfigService"/> on a real database file, with a real (unattached) <see cref="SnapshotPublisher"/> and
/// a stand-in compiler (the real one is <see cref="Paduraksa.Application.Compilation.ConfigCompiler"/>), so these tests
/// exercise the pipeline alone. The stand-in compiles every document to an empty proxy config (no
/// YARP content, so the unattached publisher makes it live at once) labelled with the revision, unless told to fail.
/// </summary>
internal sealed class ConfigServiceHarness : IAsyncDisposable
{
    private ConfigServiceHarness(TestConfigDatabase db, IConfigCommandAuthorizer? authorizer, IConfigCompiler? compiler, ConfigServiceOptions? options)
    {
        Db = db;
        Publisher = new SnapshotPublisher(Store);
        Service = new ConfigService(db.Factory, compiler ?? Compiler, Publisher, TimeProvider.System, authorizer: authorizer, options: options);
    }

    public TestConfigDatabase Db { get; }

    public FakeCompiler Compiler { get; } = new();

    public SnapshotStore Store { get; } = new();

    public SnapshotPublisher Publisher { get; }

    public ConfigService Service { get; }

    /// <param name="authorizer">Default: allow everything.</param>
    /// <param name="compiler">Default: <see cref="Compiler"/>.</param>
    /// <param name="options">Default: the service's defaults.</param>
    /// <param name="sqliteRetryWindow">
    /// Pins Microsoft.Data.Sqlite's own retry window instead of deriving it from the pragmas' busy timeout, for the test that has
    /// to tell that retry loop from the busy handler.
    /// </param>
    public static async Task<ConfigServiceHarness> CreateAsync(
        IConfigCommandAuthorizer? authorizer = null, IConfigCompiler? compiler = null, ConfigServiceOptions? options = null,
        TimeSpan? sqliteRetryWindow = null) =>
        new(await TestConfigDatabase.CreateAsync(sqliteRetryWindow), authorizer, compiler, options);

    public static CommandOrigin Admin { get; } = new(Actor.ForUser(UserId.New()), Channel.Dashboard, System.Net.IPAddress.Loopback);

    public Task<ConfigCommandResult> RunAsync(ConfigCommand command, CommandOrigin? origin = null) =>
        Service.ExecuteAsync(command, origin ?? Admin, Ct);

    public async Task<ConfigDocument> CurrentAsync() => (await Service.GetCurrentAsync(Ct)).Document;

    public async Task<long> CountAsync(string table) => (long)(await Db.ScalarAsync($"SELECT count(*) FROM {table}"))!;

    public async Task<List<AuditRecord>> AuditAsync()
    {
        await using var context = Db.NewContext();
        return [.. (await context.AuditEvents.OrderBy(e => e.Id).ToListAsync(Ct)).Select(AuditMapping.ToRecord)];
    }

    /// <summary>The first audit event matching <paramref name="match"/>, waiting for it up to 10 s (background writes).</summary>
    public async Task<AuditRecord> WaitForAuditAsync(Func<AuditRecord, bool> match)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            if ((await AuditAsync()).FirstOrDefault(match) is { } found)
            {
                return found;
            }

            Assert.True(DateTime.UtcNow < deadline, "The audit event never appeared.");
            await Task.Delay(20, Ct);
        }
    }

    public async Task<IReadOnlyList<RevisionInfo>> RevisionsAsync()
    {
        await using var context = Db.NewContext();
        return await ConfigRevisionStore.ListAsync(context, ConfigRevisionStore.MaxPageSize, cancellationToken: Ct);
    }

    public async Task<ConfigDocument> RevisionDocumentAsync(ConfigRevisionId id)
    {
        await using var context = Db.NewContext();
        var stored = await ConfigRevisionStore.GetAsync(context, id, Ct);
        return CanonicalConfigJson.Deserialize(stored!.Document.Span);
    }

    public async Task AssertChainIntactAsync()
    {
        await using var context = Db.NewContext();
        var report = await AuditChainVerifier.VerifyAsync(context, Ct);
        Assert.True(report.IsIntact, report.Break?.Description);
    }

    public async ValueTask DisposeAsync()
    {
        Service.Dispose();
        await Publisher.DisposeAsync();
        Store.Dispose();
        await Db.DisposeAsync();
    }

    /// <summary>A stand-in compiler for the pipeline tests: an empty proxy config per document.</summary>
    public sealed class FakeCompiler : IConfigCompiler
    {
        /// <summary>Errors to report instead of compiling; null or empty: compile.</summary>
        public Func<ConfigDocument, IReadOnlyList<ConfigError>?>? Fail { get; set; }

        /// <summary>Runs first on every compile: between a command's load and its commit.</summary>
        public Action<ConfigDocument>? OnCompile { get; set; }

        public List<ConfigRevisionId> Compiled { get; } = [];

        public ConfigCompilation Compile(ConfigDocument document, ConfigRevisionId revision, CertificateInventory inventory)
        {
            OnCompile?.Invoke(document);
            lock (Compiled)
            {
                Compiled.Add(revision);
            }

            if (Fail?.Invoke(document) is { Count: > 0 } errors)
            {
                return new ConfigCompilation(null, errors);
            }

            var result = SnapshotCompiler.Compile(new ProxyConfigModel { Revision = revision });
            return new ConfigCompilation(result.Snapshot, []);
        }
    }

    /// <summary>Compiles to a snapshot with YARP content, which an unattached publisher queues until it is attached
    /// (or fails it when disposed).</summary>
    public sealed class YarpfulCompiler : IConfigCompiler
    {
        public ConfigCompilation Compile(ConfigDocument document, ConfigRevisionId revision, CertificateInventory inventory)
        {
            var model = new ProxyConfigModel
            {
                Revision = revision,
                Upstreams = [new UpstreamSpec { Id = UpstreamId.New(), Destinations = [new(DestinationId.New(), "http://app:80")] }],
            };
            var snapshot = SnapshotCompiler.Compile(model).Snapshot!;
            Assert.False(snapshot.Yarp.IsEmpty);
            return new ConfigCompilation(snapshot, []);
        }
    }
}

/// <summary>Small valid config objects for the command tests.</summary>
internal static class Objects
{
    public static HostConfig RespondHost(HostId id, params string[] names) =>
        new(id, names, Kind: HostKind.Respond, Respond: new RespondConfig(200, "ok"));

    public static HostConfig ProxyHost(HostId id, string name, UpstreamId upstream, RouteId? route = null) =>
        new(id, [name], Routes: [new RouteConfig(route ?? RouteId.New(), upstream)]);

    public static UpstreamConfig Upstream(UpstreamId id, string name, string address = "http://app:8080") =>
        new(id, name, Destinations: [new DestinationConfig(DestinationId.New(), address)]);

    public static ProfileConfig Profile(ProfileId id, string name) =>
        new(id, name, Settings: new HostSettings { ForceHttps = true });
}
