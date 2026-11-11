using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Paduraksa.Application.Audit;
using Paduraksa.Application.Commands;
using Paduraksa.Application.LastKnownGood;
using Paduraksa.Application.Revisions;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Data;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Integration.Config.ConfigTestSupport;

namespace Paduraksa.Tests.Integration.Config;

public sealed class ConfigStoreEndToEndTests
{
    [Fact]
    public async Task ACommandGoesLiveThroughYarpWithinTheBudget()
    {
        // Exit criterion: change → revision → live in < 100 ms. Each round creates an upstream and a host routed to it (two
        // commits, two YARP applies) and ends when the first request for the new host comes back from the upstream.
        // Measured on a dev machine, with the rest of the suite running alongside: median 11-15 ms, max 25 ms. Only the
        // median is asserted, after a warm-up, and a slow round (a loaded CI machine) gets one more try, like the
        // pipeline's own timing test.
        var ct = TestContext.Current.CancellationToken;
        var (upstream, address) = await HeaderEchoUpstreamHost.StartAsync(null, ct);
        using var upstreamHost = upstream;
        await using var host = TestHost.Create();
        await host.StartAsync(ct);
        using var client = TestHost.CreateClient(host);

        var rounds = 0;
        async Task<double> RoundAsync()
        {
            var label = $"live{rounds++}";
            var name = $"{label}.test";
            var started = Stopwatch.GetTimestamp();
            await CommitAsync(host, new UpsertUpstreamCommand(Upstream(label, address)), ct);
            await CommitAsync(host, new UpsertHostCommand(ProxyHost(label, name, label)), ct);
            while (true)
            {
                var (status, body) = await GetAsync(client, host, name, "/first", ct);
                if (status == HttpStatusCode.OK && body.Contains("target: /first", StringComparison.Ordinal))
                {
                    return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }

                Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5), $"{name} never went live ({status}: {body})");
            }
        }

        async Task<double> MedianAsync(int count)
        {
            List<double> timings = [];
            for (var i = 0; i < count; i++)
            {
                timings.Add(await RoundAsync());
            }

            timings.Sort();
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"change → live through YARP (upstream + host): median {timings[count / 2]:F1} ms, max {timings[^1]:F1} ms, all [{string.Join(", ", timings.Select(t => t.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)))}]");
            return timings[count / 2];
        }

        await MedianAsync(3); // warm-up: JIT, SQLite page cache, YARP's first applies, the client's connection
        var median = await MedianAsync(9);
        if (median >= 100)
        {
            median = await MedianAsync(9);
        }

        Assert.True(median < 100, $"median {median:F1} ms");
        await upstreamHost.StopAsync(ct);
    }

    [Fact]
    public async Task ARollbackServesTheEarlierConfigAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = TestDataDirectories.NewPath();
        await using var host = TestHost.Create(dataDirectory: data);
        await host.StartAsync(ct);
        using var client = TestHost.CreateClient(host);

        var a = await CommitAsync(host, new ReplaceConfigCommand(new ConfigDocument(Hosts: [RespondHost("app", "app.test", "A")]), null), ct);
        Assert.Equal((HttpStatusCode.OK, "A"), await GetAsync(client, host, "app.test", "/", ct));

        var app = (await host.Shared.Config.GetCurrentAsync(ct)).Document.Hosts.Single();
        var b = await CommitAsync(host, new UpsertHostCommand(app with { Respond = new RespondConfig(200, "B") }), ct);
        Assert.Equal((HttpStatusCode.OK, "B"), await GetAsync(client, host, "app.test", "/", ct));

        var rollback = await CommitAsync(host, new RollbackConfigCommand(a.Id, b.Id), ct);
        Assert.Equal((HttpStatusCode.OK, "A"), await GetAsync(client, host, "app.test", "/", ct));
        Assert.Equal(rollback.Id, host.Shared.SnapshotStore.Current.Revision);

        await using var database = OpenDatabase(data);
        var history = await ConfigRevisionStore.ListAsync(database, 10, cancellationToken: ct);
        Assert.Equal([rollback.Id, b.Id, a.Id], history.Select(r => r.Id));
        Assert.Equal([3L, 2, 1], history.Select(r => r.Seq));
        Assert.Equal("Rollback to #1", history[0].Message);
        var audit = await AuditChainVerifier.VerifyAsync(database, ct);
        Assert.True(audit.IsIntact, audit.Break?.Description);
        // Exactly: the three config revisions, plus the process's own certificate events at startup - the local CA root and the
        // admin listener's leaf, each registered pending (certificate.add) and then active (certificate.update).
        var certificateEvents = Convert.ToInt64(await ScalarAsync(data, "SELECT count(*) FROM AuditEvents WHERE \"Action\" IN ('certificate.add', 'certificate.update')", ct));
        Assert.Equal(2 * 2, certificateEvents);
        Assert.Equal(3 + 4, audit.VerifiedEvents);
    }

    [Fact]
    public async Task ARestartRecompilesTheLatestRevision()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = TestDataDirectories.NewPath();
        RevisionRef latest;
        await using (var first = TestHost.Create(dataDirectory: data))
        {
            await first.StartAsync(ct);
            await CommitAsync(first, new UpsertHostCommand(RespondHost("one", "one.test", "one")), ct);
            latest = await CommitAsync(first, new UpsertHostCommand(RespondHost("two", "two.test", "two")), ct);
        }

        await using var second = TestHost.Create(dataDirectory: data);
        await second.StartAsync(ct);
        using var client = TestHost.CreateClient(second);
        using var adminClient = TestHost.CreateAdminClient(second);

        Assert.Equal(latest.Id, second.Shared.SnapshotStore.Current.Revision);
        Assert.Equal(new ConfigStoreStatus(ConfigSource.Database, latest), second.Shared.Config.Status);
        Assert.Equal((HttpStatusCode.OK, "one"), await GetAsync(client, second, "one.test", "/", ct));
        Assert.Equal((HttpStatusCode.OK, "two"), await GetAsync(client, second, "two.test", "/", ct));
        Assert.Equal((HttpStatusCode.OK, "ready\n"), await ReadyzAsync(adminClient, second, ct));

        // And the store works on: the next command builds on the latest revision.
        var third = await CommitAsync(second, new UpsertHostCommand(RespondHost("three", "three.test", "three")), ct);
        Assert.Equal(3, third.Seq);
        Assert.Equal((HttpStatusCode.OK, "two"), await GetAsync(client, second, "two.test", "/", ct));
    }

    [Theory]
    [InlineData("corrupt latest revision")]
    [InlineData("unreadable database file")]
    public async Task AnUnusableDatabaseStartsDegradedOnTheLastKnownGoodConfig(string damage)
    {
        var ct = TestContext.Current.CancellationToken;
        var data = TestDataDirectories.NewPath();
        RevisionRef latest;
        await using (var first = TestHost.Create(dataDirectory: data))
        {
            await first.StartAsync(ct);
            await CommitAsync(first, new UpsertHostCommand(RespondHost("app", "app.test", "A")), ct);
            var app = (await first.Shared.Config.GetCurrentAsync(ct)).Document.Hosts.Single();
            latest = await CommitAsync(first, new UpsertHostCommand(app with { Respond = new RespondConfig(200, "B") }), ct);
        }

        if (damage == "corrupt latest revision")
        {
            await ExecuteSqlAsync(data, "UPDATE ConfigRevisions SET DocumentHash = zeroblob(32) WHERE Seq = 2", ct);
        }
        else
        {
            ClearPool(data);
            File.Delete(DatabasePath(data) + "-wal");
            File.Delete(DatabasePath(data) + "-shm");
            await File.WriteAllTextAsync(DatabasePath(data), "this isn't a SQLite database, it's a grocery list\n", ct);
        }

        LogCapture logs = new();
        await using var host = TestHost.Create(dataDirectory: data, logs: logs);
        await host.StartAsync(ct);
        using var client = TestHost.CreateClient(host);
        using var adminClient = TestHost.CreateAdminClient(host);

        // The proxy serves the last-known-good config (the latest revision that went live: B) ...
        var status = host.Shared.Config.Status;
        Assert.True(status.IsDegraded);
        Assert.Equal(ConfigSource.LastKnownGood, status.Source);
        Assert.Equal(latest, status.Revision);
        Assert.False(status.AcceptsCommands);
        Assert.Equal(latest.Id, host.Shared.SnapshotStore.Current.Revision);
        Assert.Equal((HttpStatusCode.OK, "B"), await GetAsync(client, host, "app.test", "/", ct));

        // ... still ready for traffic, but says it's degraded, and logs why at Critical ...
        Assert.Equal((HttpStatusCode.OK, "degraded\n"), await ReadyzAsync(adminClient, host, ct));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Critical && e.Message.StartsWith("DEGRADED: serving the last-known-good config", StringComparison.Ordinal));

        // ... and refuses changes.
        var refused = await host.Shared.Config.ExecuteAsync(new UpsertHostCommand(RespondHost("new", "new.test", "new")), Origin, ct);
        Assert.Equal(ConfigCommandStatus.Unavailable, refused.Status);
        Assert.Equal(ConfigService.StoreUnavailableCode, Assert.Single(refused.Errors).Code);
        Assert.Equal(latest.Id, host.Shared.SnapshotStore.Current.Revision);
    }

    [Fact]
    public async Task ALatestRevisionBindingAGoneCertificateStillStartsWithAWarning()
    {
        // Revision #2 binds a certificate this process doesn't have (the in-memory test certificate died with the first
        // process, as an expired or key-less one would): the start is healthy, the host degrades to the default
        // certificate, and the problem is a warning, not a failed start.
        var ct = TestContext.Current.CancellationToken;
        var data = TestDataDirectories.NewPath();
        CertificateBinding gone = new() { Mode = CertificateBindingMode.Specific, CertificateId = TestIds.Certificate("gone") };
        await using (var first = TestHost.Create(dataDirectory: data))
        {
            await first.StartAsync(ct);
            first.Shared.Certificates.Set(TestIds.Certificate("gone"), first.Shared.Certificates.Placeholder);
            await CommitAsync(first, new UpsertHostCommand(RespondHost("one", "one.test", "one")), ct);
            await CommitAsync(first, new UpsertHostCommand(RespondHost("two", "two.test", "two") with { Certificate = gone }), ct);
        }

        await using var host = TestHost.Create(dataDirectory: data);
        await host.StartAsync(ct);
        using var client = TestHost.CreateClient(host);

        var status = host.Shared.Config.Status;
        Assert.Equal(ConfigSource.Database, status.Source);
        Assert.False(status.IsDegraded);
        Assert.Equal(2, status.Revision!.Seq);
        Assert.Equal((HttpStatusCode.OK, "one"), await GetAsync(client, host, "one.test", "/", ct));
        Assert.Equal((HttpStatusCode.OK, "two"), await GetAsync(client, host, "two.test", "/", ct));
        var warning = Assert.Single(host.Shared.Config.Warnings, w => w.Code != "certificate.auto.noCoverage"); // the other host is Auto, and nothing covers one.test
        Assert.Equal("certificate.specific.unusable", warning.Code);
        Assert.Equal("/hosts/1/certificate", warning.Path);

        // An unrelated edit of that host isn't blocked by it; binding another host to the gone certificate is.
        var two = (await host.Shared.Config.GetCurrentAsync(ct)).Document.Hosts.Single(h => h.Id == TestIds.Host("two"));
        var edited = await host.Shared.Config.ExecuteAsync(new UpsertHostCommand(two with { Notes = "edited" }), Origin, ct);
        Assert.Equal(ConfigCommandStatus.Committed, edited.Status);
        var rebound = await host.Shared.Config.ExecuteAsync(new UpsertHostCommand(RespondHost("one", "one.test", "one") with { Version = 1, Certificate = gone }), Origin, ct);
        Assert.Equal(ConfigCommandStatus.Invalid, rebound.Status);
        Assert.Equal("host.certificate.unusable", Assert.Single(rebound.Errors.Concat(rebound.RelatedErrors)).Code);
    }


    [Fact]
    public async Task ALostDatabaseIsRestoredFromLastGoodJson()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = TestDataDirectories.NewPath();
        RevisionRef lost;
        await using (var first = TestHost.Create(dataDirectory: data))
        {
            await first.StartAsync(ct);
            await CommitAsync(first, new UpsertHostCommand(RespondHost("one", "one.test", "one")), ct);
            lost = await CommitAsync(first, new UpsertHostCommand(RespondHost("two", "two.test", "two")), ct);
        }

        ClearPool(data);
        foreach (var file in Directory.GetFiles(data, ConfigDatabase.FileName + "*"))
        {
            File.Delete(file);
        }

        LogCapture logs = new();
        await using var host = TestHost.Create(dataDirectory: data, logs: logs);
        await host.StartAsync(ct);
        using var client = TestHost.CreateClient(host);
        using var adminClient = TestHost.CreateAdminClient(host);

        // The file's config is committed again as revision #1 (audited) and served, healthy.
        var status = host.Shared.Config.Status;
        Assert.False(status.IsDegraded);
        Assert.Equal(1, status.Revision!.Seq);
        Assert.Equal((HttpStatusCode.OK, "one"), await GetAsync(client, host, "one.test", "/", ct));
        Assert.Equal((HttpStatusCode.OK, "two"), await GetAsync(client, host, "two.test", "/", ct));
        Assert.Equal((HttpStatusCode.OK, "ready\n"), await ReadyzAsync(adminClient, host, ct));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains($"#{lost.Seq} ({lost.Id})", StringComparison.Ordinal));
        await using var database = OpenDatabase(data);
        var restored = Assert.Single(await ConfigRevisionStore.ListAsync(database, 10, cancellationToken: ct));
        Assert.Equal($"Restored from last-known-good #{lost.Seq} ({lost.Id})", restored.Message);
        Assert.True((await AuditChainVerifier.VerifyAsync(database, ct)).IsIntact);
    }

    [Fact]
    public async Task NothingToServeFailsTheStart()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = TestDataDirectories.NewPath();
        Directory.CreateDirectory(data);
        await File.WriteAllTextAsync(DatabasePath(data), "not a database\n", ct);
        LogCapture logs = new();
        await using var host = TestHost.Create(dataDirectory: data, logs: logs);

        Assert.Equal(ExitCodes.StartupFailed, await host.RunAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct));

        Assert.Equal(HostState.Faulted, host.State);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Critical && e.Exception is ConfigStartupException);
    }

    [Fact]
    public async Task LastGoodJsonHoldsTheDocumentOfTheLiveRevision()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = TestDataDirectories.NewPath();
        var path = Path.Combine(data, LastKnownGoodFile.FileName);
        await using var host = TestHost.Create(dataDirectory: data);
        await host.StartAsync(ct);

        // The empty config is no revision: nothing to fall back to, nothing written.
        await host.Shared.LastKnownGood.FlushAsync();
        Assert.False(File.Exists(path));

        await CommitAsync(host, new UpsertHostCommand(RespondHost("one", "one.test", "one")), ct);
        var second = await CommitAsync(host, new UpsertHostCommand(RespondHost("two", "two.test", "two")), ct);
        await host.Shared.LastKnownGood.FlushAsync();

        var file = await File.ReadAllBytesAsync(path, ct);
        using var json = JsonDocument.Parse(file);
        var root = json.RootElement;
        Assert.Equal(LastKnownGoodFile.Format, root.GetProperty("format").GetString());
        Assert.Equal(second.Id.ToString(), root.GetProperty("revision").GetString());
        Assert.Equal(2, root.GetProperty("seq").GetInt64());

        // The document is the revision's canonical bytes, verbatim: its hash is the one the database stores.
        var document = System.Text.Encoding.UTF8.GetBytes(root.GetProperty("document").GetRawText());
        await using var database = OpenDatabase(data);
        var stored = (await ConfigRevisionStore.ListAsync(database, 1, cancellationToken: ct)).Single();
        Assert.Equal(Convert.ToHexStringLower(stored.DocumentHash.Span), root.GetProperty("sha256").GetString());
        Assert.Equal(root.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(document)));
        Assert.Equal(["one.test", "two.test"], CanonicalHosts(document));

        // Written atomically: nothing left behind next to it.
        Assert.Equal([LastKnownGoodFile.FileName], Directory.GetFiles(data, "last-good*").Select(Path.GetFileName));
    }

    private static string[] CanonicalHosts(byte[] document) =>
        [.. Application.Config.CanonicalConfigJson.Deserialize(document).Hosts.SelectMany(h => h.Names).Order(StringComparer.Ordinal)];
}
