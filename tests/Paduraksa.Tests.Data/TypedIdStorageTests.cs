using Paduraksa.Core.Ids;
using Paduraksa.Tests.Data.Sample;
using static Paduraksa.Tests.Data.Sample.SampleDatabase;

namespace Paduraksa.Tests.Data;

public sealed class TypedIdStorageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 34, 56, 789, TimeSpan.Zero);

    [Fact]
    public async Task UuidV7IsA16ByteBigEndianBlobStartingWithTheTimestamp()
    {
        await using var db = await CreateAsync();
        var id = HostId.FromValue(new UuidV7Generator(new ManualTimeProvider(Now)).Next());
        await db.SaveAsync(new Host { Id = id, Name = "h" });

        var row = (await db.QueryAsync("SELECT typeof(Id), length(Id), hex(Id) FROM Hosts")).Single();

        Assert.Equal("blob", row[0]);
        Assert.Equal(16L, row[1]);
        Assert.Equal(BigEndianHex(id.Value), row[2]);
        // The first 48 bits are the Unix millisecond timestamp (RFC 9562), then version nibble 7.
        Assert.StartsWith(Now.ToUnixTimeMilliseconds().ToString("X12", null), (string)row[2]!, StringComparison.Ordinal);
        Assert.Equal('7', ((string)row[2]!)[12]);
    }

    [Fact]
    public async Task UuidV4IsA16ByteBigEndianBlob()
    {
        await using var db = await CreateAsync();
        var host = new Host { Id = HostId.New(), Name = "h" };
        var challenge = new Challenge { Id = ChallengeId.New(), HostId = host.Id };
        await db.SaveAsync(host, challenge);

        var row = (await db.QueryAsync("SELECT typeof(Id), length(Id), hex(Id), hex(HostId) FROM Challenges")).Single();

        Assert.Equal("blob", row[0]);
        Assert.Equal(16L, row[1]);
        Assert.Equal(BigEndianHex(challenge.Id.Value), row[2]);
        Assert.Equal('4', ((string)row[2]!)[12]);
        Assert.Equal(BigEndianHex(host.Id.Value), row[3]); // the FK column stores the same bytes as the key
    }

    [Fact]
    public async Task Int64OrderedIsAnIntegerHoldingTheIdValue()
    {
        await using var db = await CreateAsync();
        var first = new AuditEvent { Id = AuditEventId.New(), Action = "a" };
        var second = new AuditEvent { Id = AuditEventId.New(), Action = "b", PreviousId = first.Id };
        await db.SaveAsync(first, second);

        var row = (await db.QueryAsync($"SELECT typeof(Id), Id, rowid, typeof(PreviousId), PreviousId FROM AuditEvents WHERE Id = {second.Id.Value}")).Single();

        Assert.Equal(["integer", second.Id.Value, second.Id.Value, "integer", first.Id.Value], row);
    }

    [Fact]
    public async Task NullableIdsAreSqlNullAndColumnComplexTypesUseColumnStorage()
    {
        await using var db = await CreateAsync();
        var host = new Host { Id = HostId.New(), Name = "h" };
        var plain = new Route { Id = RouteId.New(), HostId = host.Id };
        var copied = new Route
        {
            Id = RouteId.New(),
            HostId = host.Id,
            Origin = { CopiedFromRouteId = plain.Id, CreatedByAuditId = AuditEventId.New() },
        };
        await db.SaveAsync(host, plain, copied);

        var rows = await db.QueryAsync(
            "SELECT typeof(ChallengeId), typeof(Origin_CopiedFromRouteId), hex(Origin_CopiedFromRouteId), typeof(Origin_CreatedByAuditId), Origin_CreatedByAuditId "
            + $"FROM Routes ORDER BY Id");

        Assert.Equal(["null", "null", "", "null", null], rows[0]);
        Assert.Equal(["null", "blob", BigEndianHex(plain.Id.Value), "integer", copied.Origin.CreatedByAuditId!.Value.Value], rows[1]);
    }

    [Fact]
    public async Task InsideJsonEveryKindIsItsCanonicalString()
    {
        await using var db = await CreateAsync();
        var settings = new HostSettings
        {
            DefaultRouteId = RouteId.New(),
            ChallengeId = ChallengeId.New(),
            LastAuditId = AuditEventId.New(),
            Challenge = { FirstAuditId = AuditEventId.New() },
            Aliases = { new HostAlias { TargetHostId = HostId.New() } },
        };
        await db.SaveAsync(new Host { Id = HostId.New(), Name = "h", Settings = settings });

        string[] paths = ["$.DefaultRouteId", "$.ChallengeId", "$.LastAuditId", "$.Challenge.FirstAuditId", "$.Aliases[0].TargetHostId"];
        string[] expected =
        [
            settings.DefaultRouteId.ToString(), settings.ChallengeId.Value.ToString(), settings.LastAuditId.Value.ToString(),
            settings.Challenge.FirstAuditId.ToString(), settings.Aliases[0].TargetHostId.ToString(),
        ];
        foreach (var (path, value) in paths.Zip(expected))
        {
            var row = (await db.QueryAsync($"SELECT json_type(Settings, '{path}'), json_extract(Settings, '{path}') FROM Hosts")).Single();
            Assert.Equal(["text", value], row);
        }

        Assert.Equal("null", await db.ScalarAsync("SELECT json_type(Settings, '$.FallbackHostId') FROM Hosts"));
        Assert.Equal("null", await db.ScalarAsync("SELECT json_type(Settings, '$.Aliases[0].AddedByAuditId') FROM Hosts"));
        Assert.StartsWith("rte_", settings.DefaultRouteId.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Int64OrderedKeyIsTheRowidAliasWithoutAutoincrement()
    {
        await using var db = await CreateAsync();
        var ddl = (string)(await db.ScalarAsync("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'AuditEvents'"))!;

        Assert.Contains("\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_AuditEvents\" PRIMARY KEY", ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("AUTOINCREMENT", ddl, StringComparison.OrdinalIgnoreCase);
        // A rowid-alias key has no separate unique index; a BLOB key does.
        Assert.Equal(0L, await db.ScalarAsync("SELECT count(*) FROM sqlite_master WHERE type = 'index' AND tbl_name = 'AuditEvents' AND name LIKE 'sqlite_autoindex%'"));
        Assert.Equal(1L, await db.ScalarAsync("SELECT count(*) FROM sqlite_master WHERE type = 'index' AND tbl_name = 'Hosts' AND name LIKE 'sqlite_autoindex%'"));
        Assert.Contains("USING INTEGER PRIMARY KEY (rowid=?)", await db.PlanAsync("SELECT Action FROM AuditEvents WHERE Id = 42"), StringComparison.Ordinal);
    }

    private static string BigEndianHex(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        Assert.True(value.TryWriteBytes(bytes, bigEndian: true, out _));
        return Convert.ToHexString(bytes);
    }
}
