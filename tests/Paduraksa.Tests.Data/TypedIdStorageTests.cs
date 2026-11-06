using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Paduraksa.Core.Ids;
using Paduraksa.Data.Ids;
using static Paduraksa.Tests.Data.SampleDatabase;

namespace Paduraksa.Tests.Data;

/// <summary>What actually lands in SQLite, inspected with raw SQL (<c>typeof</c>, <c>hex</c>, <c>json_*</c>, schema).</summary>
public sealed class TypedIdStorageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 34, 56, 789, TimeSpan.Zero);

    [Fact]
    public async Task UuidV7_IsA16ByteBigEndianBlob_StartingWithTheTimestamp()
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
    public async Task UuidV4_IsA16ByteBigEndianBlob()
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
    public async Task Int64Ordered_IsAnInteger_HoldingTheIdValue()
    {
        await using var db = await CreateAsync();
        var first = new AuditEvent { Id = AuditEventId.New(), Action = "a" };
        var second = new AuditEvent { Id = AuditEventId.New(), Action = "b", PreviousId = first.Id };
        await db.SaveAsync(first, second);

        var row = (await db.QueryAsync($"SELECT typeof(Id), Id, rowid, typeof(PreviousId), PreviousId FROM AuditEvents WHERE Id = {second.Id.Value}")).Single();

        Assert.Equal(["integer", second.Id.Value, second.Id.Value, "integer", first.Id.Value], row);
    }

    [Fact]
    public async Task NullableIds_AreSqlNull_AndColumnComplexTypesUseColumnStorage()
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
    public async Task InsideJson_EveryKindIsItsCanonicalString()
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
    public async Task Int64OrderedKey_IsTheRowidAlias_WithoutAutoincrement()
    {
        await using var db = await CreateAsync();
        var ddl = (string)(await db.ScalarAsync("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'AuditEvents'"))!;

        Assert.Contains("\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_AuditEvents\" PRIMARY KEY", ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("AUTOINCREMENT", ddl, StringComparison.OrdinalIgnoreCase);
        // A rowid-alias key has no separate unique index; a BLOB key does.
        Assert.Equal(0L, await db.ScalarAsync("SELECT count(*) FROM sqlite_master WHERE type = 'index' AND tbl_name = 'AuditEvents' AND name LIKE 'sqlite_autoindex%'"));
        Assert.Equal(1L, await db.ScalarAsync("SELECT count(*) FROM sqlite_master WHERE type = 'index' AND tbl_name = 'Hosts' AND name LIKE 'sqlite_autoindex%'"));
        var plan = await db.QueryAsync("EXPLAIN QUERY PLAN SELECT Action FROM AuditEvents WHERE Id = 42");
        Assert.Contains(plan, row => ((string)row[3]!).Contains("USING INTEGER PRIMARY KEY (rowid=?)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Int64OrderedKey_IsNotGeneratedByTheDatabase()
    {
        await using var db = await CreateAsync();
        var id = AuditEventId.FromValue(123);
        await db.SaveAsync(new AuditEvent { Id = id, Action = "a" });

        Assert.Equal(123L, await db.ScalarAsync("SELECT Id FROM AuditEvents"));
    }

    [Fact]
    public async Task EveryTypedIdProperty_HasTheConventionConverterAndIsNeverGenerated()
    {
        await using var db = await CreateAsync();
        await using var context = db.NewContext();

        // The design-time model keeps the configured comparer (the runtime model only has EF's nullable wrapper).
        var properties = context.GetService<IDesignTimeModel>().Model.GetEntityTypes().SelectMany(AllProperties).Where(IsTypedId).ToList();

        Assert.Equal(20, properties.Count); // every typed ID in SampleModel, incl. complex and JSON members
        foreach (var property in properties)
        {
            var idType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
            var expected = property.DeclaringType is IComplexType complex && complex.IsMappedToJson()
                ? typeof(TypedIdToStringConverter<>)
                : ValueTypeOf(idType) == typeof(long)
                    ? typeof(TypedIdToInt64Converter<>)
                    : typeof(TypedIdToBytesConverter<>);
            Assert.Equal(expected.MakeGenericType(idType), property.GetValueConverter()?.GetType());
            Assert.Equal(typeof(TypedIdValueComparer<>).MakeGenericType(idType), property.FindAnnotation("ValueComparer")?.Value?.GetType());
            Assert.Equal(ValueGenerated.Never, property.ValueGenerated);
        }
    }

    private static IEnumerable<IProperty> AllProperties(ITypeBase type) =>
        type.GetDeclaredProperties().Concat(type.GetDeclaredComplexProperties().SelectMany(c => AllProperties(c.ComplexType)));

    private static bool IsTypedId(IReadOnlyProperty property) =>
        ValueTypeOf(Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType) is not null;

    /// <summary>The <c>TValue</c> of <c>ITypedId&lt;TSelf, TValue&gt;</c>, or null for other types.</summary>
    private static Type? ValueTypeOf(Type type) =>
        type.GetInterfaces().SingleOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ITypedId<,>))?.GenericTypeArguments[1];

    private static string BigEndianHex(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        Assert.True(value.TryWriteBytes(bytes, bigEndian: true, out _));
        return Convert.ToHexString(bytes);
    }
}
