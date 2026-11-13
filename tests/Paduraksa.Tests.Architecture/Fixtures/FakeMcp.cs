// Deliberately violating fixture for the architecture sanity tests. NOT production code.
// Depends on a fake type in every other layer, on EF Core, on System.Linq and on a non-layer Paduraksa type,
// so every rule that forbids an edge can be shown to fire. Lives only in the test assembly and
// is never loaded into the real solution model. All Fake<Layer>.cs files share this shape.
using Microsoft.EntityFrameworkCore;
using Paduraksa.Acme.ArchFixtures;
using Paduraksa.Admin.ArchFixtures;
using Paduraksa.Application.ArchFixtures;
using Paduraksa.Contracts.ArchFixtures;
using Paduraksa.Core.ArchFixtures;
using Paduraksa.Dashboard.ArchFixtures;
using Paduraksa.Data.ArchFixtures;
using Paduraksa.Notifications.ArchFixtures;
using Paduraksa.Proxy.ArchFixtures;
using Paduraksa.ProxyProtocol.ArchFixtures;
using Paduraksa.ProxyProtocol.AspNetCore.ArchFixtures;
using Paduraksa.Server.ArchFixtures;
using Paduraksa.Storage.ArchFixtures;
using Paduraksa.Tests.Architecture.Fixtures;
using Paduraksa.Vault.ArchFixtures;

namespace Paduraksa.Mcp.ArchFixtures;

public sealed class FakeMcp
{
    public FakeCore? Core { get; set; }

    public FakeProxyProtocol? ProxyProtocol { get; set; }

    public FakeProxyProtocolAspNetCore? ProxyProtocolAspNetCore { get; set; }

    public FakeNotifications? Notifications { get; set; }

    public FakeContracts? Contracts { get; set; }

    public FakeStorage? Storage { get; set; }

    public FakeData? Data { get; set; }

    public FakeVault? Vault { get; set; }

    public FakeProxy? Proxy { get; set; }

    public FakeAcme? Acme { get; set; }

    public FakeApplication? Application { get; set; }

    public FakeAdmin? Admin { get; set; }

    public FakeDashboard? Dashboard { get; set; }

    public FakeServer? Server { get; set; }

    public DbContext? Db { get; set; }

    public ILookup<int, int>? Lookup { get; set; }

    public static int CountPositive(int[] values) => values.Count(v => v > 0);

    public NonLayerType? NotALayer { get; set; }
}
