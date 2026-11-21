using System.Security.Authentication;

using Paduraksa.Application.Honeypot;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Honeypot;
using Paduraksa.ProxyProtocol;
using Paduraksa.Tests.Data.Config;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Data.Honeypot;

/// <summary>A recorder, its store and read service over one migrated database, and a clock they share.</summary>
internal sealed class HoneypotRecordingRig : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);
    public static readonly HostId Host = HostId.New();

    private HoneypotRecordingRig(TestConfigDatabase database)
    {
        Database = database;
        Time = new ManualTimeProvider(Start);
        Stats = new HoneypotStats();
        Recorder = new HoneypotRecorder(Stats, Time);
        Store = new HoneypotStore(database.Factory, Recorder);
        Service = new HoneypotService(database.Factory, Stats, Time);
    }

    public TestConfigDatabase Database { get; }

    public ManualTimeProvider Time { get; }

    public HoneypotStats Stats { get; }

    public HoneypotRecorder Recorder { get; }

    public HoneypotStore Store { get; }

    public HoneypotService Service { get; }

    public static async Task<HoneypotRecordingRig> CreateAsync() => new(await TestConfigDatabase.CreateAsync());

    /// <summary>A rig whose database has no tables, so every write fails.</summary>
    public static HoneypotRecordingRig Down() => new(TestConfigDatabase.Empty());

    public static HoneypotHit Hit(
        TimeSpan after = default,
        string client = "203.0.113.9",
        string agent = "ExampleBot/1.0",
        HostId? host = null,
        HoneypotPageKind page = HoneypotPageKind.Post,
        HoneypotLinkKind link = HoneypotLinkKind.Normal,
        ulong id = 1,
        ulong parent = 0,
        HoneypotPowOutcome pow = HoneypotPowOutcome.None,
        string canary = "amber falcon",
        int status = 200,
        string? referer = "https://example.test/",
        string path = "/archive-k7f3/",
        bool pathTruncated = false) =>
        new(
            Start + after, host ?? Host, IpKey.Parse(client), agent, canary, HoneypotEntry.Robots, page, link, id, parent, status, pow, pow == HoneypotPowOutcome.Solved ? 3_000 : 0,
            "HTTP/2", SslProtocols.Tls13, "text/html", "en-US", "none", "navigate", "document", "?1", referer, path, pathTruncated);

    /// <summary>Queues the hits, folds them and writes.</summary>
    public async Task RecordAsync(params HoneypotHit[] hits)
    {
        foreach (var hit in hits)
        {
            Recorder.Record(in hit);
        }

        await Store.WritePendingAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => Database.DisposeAsync();
}
