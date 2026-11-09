using Microsoft.EntityFrameworkCore;
using Paduraksa.Tests.Data.Sample;
using static Paduraksa.Tests.Data.Sample.SampleDatabase;

namespace Paduraksa.Tests.Data;

public sealed class TypedIdCollectionTests
{
    [Fact]
    public async Task ColumnListRoundTripsAsAJsonArrayOfStrings()
    {
        await using var db = await CreateAsync();
        var host = new Host { Id = HostId.New(), Name = "h" };
        var related = new List<HostId> { HostId.New(), host.Id };
        var route = new Route { Id = RouteId.New(), HostId = host.Id, RelatedHostIds = related };
        await db.SaveAsync(host, route);

        Assert.Equal($"[\"{related[0]}\",\"{related[1]}\"]", await db.ScalarAsync("SELECT RelatedHostIds FROM Routes"));
        await using var context = db.NewContext();
        Assert.Equal(related, (await context.Routes.SingleAsync(Ct)).RelatedHostIds);
    }

    [Fact]
    public async Task JsonListRoundTripsAsAJsonArrayOfStrings()
    {
        await using var db = await CreateAsync();
        var allowed = new List<RouteId> { RouteId.New(), RouteId.New() };
        await db.SaveAsync(new Host { Id = HostId.New(), Name = "h", Settings = { AllowedRouteIds = allowed } });

        Assert.Equal(
            $"[\"{allowed[0]}\",\"{allowed[1]}\"]",
            await db.ScalarAsync("SELECT json_extract(Settings, '$.AllowedRouteIds') FROM Hosts"));
        await using var context = db.NewContext();
        Assert.Equal(allowed, (await context.Hosts.SingleAsync(Ct)).Settings.AllowedRouteIds);
    }

    [Fact]
    public async Task ContainsOnAListTranslatesToJsonEach()
    {
        await using var db = await CreateAsync();
        var host = new Host { Id = HostId.New(), Name = "h", Settings = { AllowedRouteIds = [RouteId.New()] } };
        var other = HostId.New();
        await db.SaveAsync(host, new Route { Id = RouteId.New(), HostId = host.Id, RelatedHostIds = [other] });

        await using var context = db.NewContext();
        var routes = context.Routes.Where(r => r.RelatedHostIds.Contains(other));
        var allowedRouteId = host.Settings.AllowedRouteIds[0];
        var hosts = context.Hosts.Where(h => h.Settings.AllowedRouteIds.Contains(allowedRouteId));

        Assert.Contains("json_each(\"r\".\"RelatedHostIds\")", routes.ToQueryString(), StringComparison.Ordinal);
        Assert.Contains("json_each(\"h\".\"Settings\" ->> 'AllowedRouteIds')", hosts.ToQueryString(), StringComparison.Ordinal);
        Assert.Equal(1, await routes.CountAsync(Ct));
        Assert.Equal(1, await hosts.CountAsync(Ct));
        Assert.Equal(0, await context.Routes.CountAsync(r => r.RelatedHostIds.Contains(host.Id), Ct));
    }
}
