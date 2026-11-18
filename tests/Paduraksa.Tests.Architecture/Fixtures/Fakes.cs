// Deliberately violating fixtures for the architecture sanity tests. NOT production code.
// Each Paduraksa.<Layer>.ArchFixtures.Fake<Layer> depends on a fake type in every other layer, on EF Core,
// on System.Linq and on a non-layer Paduraksa type, so every rule that forbids an edge can be shown to fire.
// Lives only in the test assembly and is never loaded into the real solution model. DependencyRuleTests
// finds the types by that namespace and name.
using Microsoft.EntityFrameworkCore;
using Paduraksa.Acme.ArchFixtures;
using Paduraksa.Admin.ArchFixtures;
using Paduraksa.Application.ArchFixtures;
using Paduraksa.Contracts.ArchFixtures;
using Paduraksa.Core.ArchFixtures;
using Paduraksa.Dashboard.ArchFixtures;
using Paduraksa.Data.ArchFixtures;
using Paduraksa.Mcp.ArchFixtures;
using Paduraksa.Notifications.ArchFixtures;
using Paduraksa.Proxy.ArchFixtures;
using Paduraksa.ProxyProtocol.ArchFixtures;
using Paduraksa.ProxyProtocol.AspNetCore.ArchFixtures;
using Paduraksa.Server.ArchFixtures;
using Paduraksa.Storage.ArchFixtures;
using Paduraksa.Tests.Architecture.Fixtures;
using Paduraksa.TypedIds.ArchFixtures;
using Paduraksa.TypedIds.EntityFrameworkCore.ArchFixtures;
using Paduraksa.Vault.ArchFixtures;

namespace Paduraksa.Acme.ArchFixtures
{
    public sealed class FakeAcme
    {
        public FakeAdmin? Admin { get; set; }
        public FakeApplication? Application { get; set; }
        public FakeContracts? Contracts { get; set; }
        public FakeCore? Core { get; set; }
        public FakeDashboard? Dashboard { get; set; }
        public FakeData? Data { get; set; }
        public FakeMcp? Mcp { get; set; }
        public FakeNotifications? Notifications { get; set; }
        public FakeProxy? Proxy { get; set; }
        public FakeProxyProtocol? ProxyProtocol { get; set; }
        public FakeProxyProtocolAspNetCore? ProxyProtocolAspNetCore { get; set; }
        public FakeServer? Server { get; set; }
        public FakeStorage? Storage { get; set; }
        public FakeTypedIds? TypedIds { get; set; }
        public FakeTypedIdsEntityFrameworkCore? TypedIdsEntityFrameworkCore { get; set; }
        public FakeVault? Vault { get; set; }
        public DbContext? Db { get; set; }
        public ILookup<int, int>? Lookup { get; set; }
        public NonLayerType? NotALayer { get; set; }
        public static int CountPositive(int[] values) => values.Count(v => v > 0);
    }
}

namespace Paduraksa.Admin.ArchFixtures
{
    public sealed class FakeAdmin
    {
        public FakeAcme? Acme { get; set; }
        public FakeApplication? Application { get; set; }
        public FakeContracts? Contracts { get; set; }
        public FakeCore? Core { get; set; }
        public FakeDashboard? Dashboard { get; set; }
        public FakeData? Data { get; set; }
        public FakeMcp? Mcp { get; set; }
        public FakeNotifications? Notifications { get; set; }
        public FakeProxy? Proxy { get; set; }
        public FakeProxyProtocol? ProxyProtocol { get; set; }
        public FakeProxyProtocolAspNetCore? ProxyProtocolAspNetCore { get; set; }
        public FakeServer? Server { get; set; }
        public FakeStorage? Storage { get; set; }
        public FakeTypedIds? TypedIds { get; set; }
        public FakeTypedIdsEntityFrameworkCore? TypedIdsEntityFrameworkCore { get; set; }
        public FakeVault? Vault { get; set; }
        public DbContext? Db { get; set; }
        public ILookup<int, int>? Lookup { get; set; }
        public NonLayerType? NotALayer { get; set; }
        public static int CountPositive(int[] values) => values.Count(v => v > 0);
    }
}

namespace Paduraksa.Application.ArchFixtures
{
    public sealed class FakeApplication
    {
        public FakeAcme? Acme { get; set; }
        public FakeAdmin? Admin { get; set; }
        public FakeContracts? Contracts { get; set; }
        public FakeCore? Core { get; set; }
        public FakeDashboard? Dashboard { get; set; }
        public FakeData? Data { get; set; }
        public FakeMcp? Mcp { get; set; }
        public FakeNotifications? Notifications { get; set; }
        public FakeProxy? Proxy { get; set; }
        public FakeProxyProtocol? ProxyProtocol { get; set; }
        public FakeProxyProtocolAspNetCore? ProxyProtocolAspNetCore { get; set; }
        public FakeServer? Server { get; set; }
        public FakeStorage? Storage { get; set; }
        public FakeTypedIds? TypedIds { get; set; }
        public FakeTypedIdsEntityFrameworkCore? TypedIdsEntityFrameworkCore { get; set; }
        public FakeVault? Vault { get; set; }
        public DbContext? Db { get; set; }
        public ILookup<int, int>? Lookup { get; set; }
        public NonLayerType? NotALayer { get; set; }
        public static int CountPositive(int[] values) => values.Count(v => v > 0);
    }
}

namespace Paduraksa.Contracts.ArchFixtures
{
    public sealed class FakeContracts
    {
        public FakeAcme? Acme { get; set; }
        public FakeAdmin? Admin { get; set; }
        public FakeApplication? Application { get; set; }
        public FakeCore? Core { get; set; }
        public FakeDashboard? Dashboard { get; set; }
        public FakeData? Data { get; set; }
        public FakeMcp? Mcp { get; set; }
        public FakeNotifications? Notifications { get; set; }
        public FakeProxy? Proxy { get; set; }
        public FakeProxyProtocol? ProxyProtocol { get; set; }
        public FakeProxyProtocolAspNetCore? ProxyProtocolAspNetCore { get; set; }
        public FakeServer? Server { get; set; }
        public FakeStorage? Storage { get; set; }
        public FakeTypedIds? TypedIds { get; set; }
        public FakeTypedIdsEntityFrameworkCore? TypedIdsEntityFrameworkCore { get; set; }
        public FakeVault? Vault { get; set; }
        public DbContext? Db { get; set; }
        public ILookup<int, int>? Lookup { get; set; }
        public NonLayerType? NotALayer { get; set; }
        public static int CountPositive(int[] values) => values.Count(v => v > 0);
    }
}

namespace Paduraksa.Core.ArchFixtures
{
    public sealed class FakeCore
    {
        public FakeAcme? Acme { get; set; }
        public FakeAdmin? Admin { get; set; }
        public FakeApplication? Application { get; set; }
        public FakeContracts? Contracts { get; set; }
        public FakeDashboard? Dashboard { get; set; }
        public FakeData? Data { get; set; }
        public FakeMcp? Mcp { get; set; }
        public FakeNotifications? Notifications { get; set; }
        public FakeProxy? Proxy { get; set; }
        public FakeProxyProtocol? ProxyProtocol { get; set; }
        public FakeProxyProtocolAspNetCore? ProxyProtocolAspNetCore { get; set; }
        public FakeServer? Server { get; set; }
        public FakeStorage? Storage { get; set; }
        public FakeTypedIds? TypedIds { get; set; }
        public FakeTypedIdsEntityFrameworkCore? TypedIdsEntityFrameworkCore { get; set; }
        public FakeVault? Vault { get; set; }
        public DbContext? Db { get; set; }
        public ILookup<int, int>? Lookup { get; set; }
        public NonLayerType? NotALayer { get; set; }
        public static int CountPositive(int[] values) => values.Count(v => v > 0);
    }
}

namespace Paduraksa.Dashboard.ArchFixtures
{
    public sealed class FakeDashboard
    {
        public FakeAcme? Acme { get; set; }
        public FakeAdmin? Admin { get; set; }
        public FakeApplication? Application { get; set; }
        public FakeContracts? Contracts { get; set; }
        public FakeCore? Core { get; set; }
        public FakeData? Data { get; set; }
        public FakeMcp? Mcp { get; set; }
        public FakeNotifications? Notifications { get; set; }
        public FakeProxy? Proxy { get; set; }
        public FakeProxyProtocol? ProxyProtocol { get; set; }
        public FakeProxyProtocolAspNetCore? ProxyProtocolAspNetCore { get; set; }
        public FakeServer? Server { get; set; }
        public FakeStorage? Storage { get; set; }
        public FakeTypedIds? TypedIds { get; set; }
        public FakeTypedIdsEntityFrameworkCore? TypedIdsEntityFrameworkCore { get; set; }
        public FakeVault? Vault { get; set; }
        public DbContext? Db { get; set; }
        public ILookup<int, int>? Lookup { get; set; }
        public NonLayerType? NotALayer { get; set; }
        public static int CountPositive(int[] values) => values.Count(v => v > 0);
    }
}

namespace Paduraksa.Data.ArchFixtures
{
    public sealed class FakeData
    {
        public FakeAcme? Acme { get; set; }
        public FakeAdmin? Admin { get; set; }
        public FakeApplication? Application { get; set; }
        public FakeContracts? Contracts { get; set; }
        public FakeCore? Core { get; set; }
        public FakeDashboard? Dashboard { get; set; }
        public FakeMcp? Mcp { get; set; }
        public FakeNotifications? Notifications { get; set; }
        public FakeProxy? Proxy { get; set; }
        public FakeProxyProtocol? ProxyProtocol { get; set; }
        public FakeProxyProtocolAspNetCore? ProxyProtocolAspNetCore { get; set; }
        public FakeServer? Server { get; set; }
        public FakeStorage? Storage { get; set; }
        public FakeTypedIds? TypedIds { get; set; }
        public FakeTypedIdsEntityFrameworkCore? TypedIdsEntityFrameworkCore { get; set; }
        public FakeVault? Vault { get; set; }
        public DbContext? Db { get; set; }
        public ILookup<int, int>? Lookup { get; set; }
        public NonLayerType? NotALayer { get; set; }
        public static int CountPositive(int[] values) => values.Count(v => v > 0);
    }
}

namespace Paduraksa.Mcp.ArchFixtures
{
    public sealed class FakeMcp
    {
        public FakeAcme? Acme { get; set; }
        public FakeAdmin? Admin { get; set; }
        public FakeApplication? Application { get; set; }
        public FakeContracts? Contracts { get; set; }
        public FakeCore? Core { get; set; }
        public FakeDashboard? Dashboard { get; set; }
        public FakeData? Data { get; set; }
        public FakeNotifications? Notifications { get; set; }
        public FakeProxy? Proxy { get; set; }
        public FakeProxyProtocol? ProxyProtocol { get; set; }
        public FakeProxyProtocolAspNetCore? ProxyProtocolAspNetCore { get; set; }
        public FakeServer? Server { get; set; }
        public FakeStorage? Storage { get; set; }
        public FakeTypedIds? TypedIds { get; set; }
        public FakeTypedIdsEntityFrameworkCore? TypedIdsEntityFrameworkCore { get; set; }
        public FakeVault? Vault { get; set; }
        public DbContext? Db { get; set; }
        public ILookup<int, int>? Lookup { get; set; }
        public NonLayerType? NotALayer { get; set; }
        public static int CountPositive(int[] values) => values.Count(v => v > 0);
    }
}

namespace Paduraksa.Notifications.ArchFixtures
{
    public sealed class FakeNotifications
    {
        public FakeAcme? Acme { get; set; }
        public FakeAdmin? Admin { get; set; }
        public FakeApplication? Application { get; set; }
        public FakeContracts? Contracts { get; set; }
        public FakeCore? Core { get; set; }
        public FakeDashboard? Dashboard { get; set; }
        public FakeData? Data { get; set; }
        public FakeMcp? Mcp { get; set; }
        public FakeProxy? Proxy { get; set; }
        public FakeProxyProtocol? ProxyProtocol { get; set; }
        public FakeProxyProtocolAspNetCore? ProxyProtocolAspNetCore { get; set; }
        public FakeServer? Server { get; set; }
        public FakeStorage? Storage { get; set; }
        public FakeTypedIds? TypedIds { get; set; }
        public FakeTypedIdsEntityFrameworkCore? TypedIdsEntityFrameworkCore { get; set; }
        public FakeVault? Vault { get; set; }
        public DbContext? Db { get; set; }
        public ILookup<int, int>? Lookup { get; set; }
        public NonLayerType? NotALayer { get; set; }
        public static int CountPositive(int[] values) => values.Count(v => v > 0);
    }
}

namespace Paduraksa.Proxy.ArchFixtures
{
    public sealed class FakeProxy
    {
        public FakeAcme? Acme { get; set; }
        public FakeAdmin? Admin { get; set; }
        public FakeApplication? Application { get; set; }
        public FakeContracts? Contracts { get; set; }
        public FakeCore? Core { get; set; }
        public FakeDashboard? Dashboard { get; set; }
        public FakeData? Data { get; set; }
        public FakeMcp? Mcp { get; set; }
        public FakeNotifications? Notifications { get; set; }
        public FakeProxyProtocol? ProxyProtocol { get; set; }
        public FakeProxyProtocolAspNetCore? ProxyProtocolAspNetCore { get; set; }
        public FakeServer? Server { get; set; }
        public FakeStorage? Storage { get; set; }
        public FakeTypedIds? TypedIds { get; set; }
        public FakeTypedIdsEntityFrameworkCore? TypedIdsEntityFrameworkCore { get; set; }
        public FakeVault? Vault { get; set; }
        public DbContext? Db { get; set; }
        public ILookup<int, int>? Lookup { get; set; }
        public NonLayerType? NotALayer { get; set; }
        public static int CountPositive(int[] values) => values.Count(v => v > 0);
    }
}

namespace Paduraksa.ProxyProtocol.ArchFixtures
{
    public sealed class FakeProxyProtocol
    {
        public FakeAcme? Acme { get; set; }
        public FakeAdmin? Admin { get; set; }
        public FakeApplication? Application { get; set; }
        public FakeContracts? Contracts { get; set; }
        public FakeCore? Core { get; set; }
        public FakeDashboard? Dashboard { get; set; }
        public FakeData? Data { get; set; }
        public FakeMcp? Mcp { get; set; }
        public FakeNotifications? Notifications { get; set; }
        public FakeProxy? Proxy { get; set; }
        public FakeProxyProtocolAspNetCore? ProxyProtocolAspNetCore { get; set; }
        public FakeServer? Server { get; set; }
        public FakeStorage? Storage { get; set; }
        public FakeTypedIds? TypedIds { get; set; }
        public FakeTypedIdsEntityFrameworkCore? TypedIdsEntityFrameworkCore { get; set; }
        public FakeVault? Vault { get; set; }
        public DbContext? Db { get; set; }
        public ILookup<int, int>? Lookup { get; set; }
        public NonLayerType? NotALayer { get; set; }
        public static int CountPositive(int[] values) => values.Count(v => v > 0);
    }
}

namespace Paduraksa.ProxyProtocol.AspNetCore.ArchFixtures
{
    public sealed class FakeProxyProtocolAspNetCore
    {
        public FakeAcme? Acme { get; set; }
        public FakeAdmin? Admin { get; set; }
        public FakeApplication? Application { get; set; }
        public FakeContracts? Contracts { get; set; }
        public FakeCore? Core { get; set; }
        public FakeDashboard? Dashboard { get; set; }
        public FakeData? Data { get; set; }
        public FakeMcp? Mcp { get; set; }
        public FakeNotifications? Notifications { get; set; }
        public FakeProxy? Proxy { get; set; }
        public FakeProxyProtocol? ProxyProtocol { get; set; }
        public FakeServer? Server { get; set; }
        public FakeStorage? Storage { get; set; }
        public FakeTypedIds? TypedIds { get; set; }
        public FakeTypedIdsEntityFrameworkCore? TypedIdsEntityFrameworkCore { get; set; }
        public FakeVault? Vault { get; set; }
        public DbContext? Db { get; set; }
        public ILookup<int, int>? Lookup { get; set; }
        public NonLayerType? NotALayer { get; set; }
        public static int CountPositive(int[] values) => values.Count(v => v > 0);
    }
}

namespace Paduraksa.Server.ArchFixtures
{
    public sealed class FakeServer
    {
        public FakeAcme? Acme { get; set; }
        public FakeAdmin? Admin { get; set; }
        public FakeApplication? Application { get; set; }
        public FakeContracts? Contracts { get; set; }
        public FakeCore? Core { get; set; }
        public FakeDashboard? Dashboard { get; set; }
        public FakeData? Data { get; set; }
        public FakeMcp? Mcp { get; set; }
        public FakeNotifications? Notifications { get; set; }
        public FakeProxy? Proxy { get; set; }
        public FakeProxyProtocol? ProxyProtocol { get; set; }
        public FakeProxyProtocolAspNetCore? ProxyProtocolAspNetCore { get; set; }
        public FakeStorage? Storage { get; set; }
        public FakeTypedIds? TypedIds { get; set; }
        public FakeTypedIdsEntityFrameworkCore? TypedIdsEntityFrameworkCore { get; set; }
        public FakeVault? Vault { get; set; }
        public DbContext? Db { get; set; }
        public ILookup<int, int>? Lookup { get; set; }
        public NonLayerType? NotALayer { get; set; }
        public static int CountPositive(int[] values) => values.Count(v => v > 0);
    }
}

namespace Paduraksa.Storage.ArchFixtures
{
    public sealed class FakeStorage
    {
        public FakeAcme? Acme { get; set; }
        public FakeAdmin? Admin { get; set; }
        public FakeApplication? Application { get; set; }
        public FakeContracts? Contracts { get; set; }
        public FakeCore? Core { get; set; }
        public FakeDashboard? Dashboard { get; set; }
        public FakeData? Data { get; set; }
        public FakeMcp? Mcp { get; set; }
        public FakeNotifications? Notifications { get; set; }
        public FakeProxy? Proxy { get; set; }
        public FakeProxyProtocol? ProxyProtocol { get; set; }
        public FakeProxyProtocolAspNetCore? ProxyProtocolAspNetCore { get; set; }
        public FakeServer? Server { get; set; }
        public FakeTypedIds? TypedIds { get; set; }
        public FakeTypedIdsEntityFrameworkCore? TypedIdsEntityFrameworkCore { get; set; }
        public FakeVault? Vault { get; set; }
        public DbContext? Db { get; set; }
        public ILookup<int, int>? Lookup { get; set; }
        public NonLayerType? NotALayer { get; set; }
        public static int CountPositive(int[] values) => values.Count(v => v > 0);
    }
}

namespace Paduraksa.Vault.ArchFixtures
{
    public sealed class FakeVault
    {
        public FakeAcme? Acme { get; set; }
        public FakeAdmin? Admin { get; set; }
        public FakeApplication? Application { get; set; }
        public FakeContracts? Contracts { get; set; }
        public FakeCore? Core { get; set; }
        public FakeDashboard? Dashboard { get; set; }
        public FakeData? Data { get; set; }
        public FakeMcp? Mcp { get; set; }
        public FakeNotifications? Notifications { get; set; }
        public FakeProxy? Proxy { get; set; }
        public FakeProxyProtocol? ProxyProtocol { get; set; }
        public FakeProxyProtocolAspNetCore? ProxyProtocolAspNetCore { get; set; }
        public FakeServer? Server { get; set; }
        public FakeStorage? Storage { get; set; }
        public FakeTypedIds? TypedIds { get; set; }
        public FakeTypedIdsEntityFrameworkCore? TypedIdsEntityFrameworkCore { get; set; }
        public DbContext? Db { get; set; }
        public ILookup<int, int>? Lookup { get; set; }
        public NonLayerType? NotALayer { get; set; }
        public static int CountPositive(int[] values) => values.Count(v => v > 0);
    }
}

namespace Paduraksa.TypedIds.ArchFixtures
{
    public sealed class FakeTypedIds
    {
        public FakeAcme? Acme { get; set; }
        public FakeAdmin? Admin { get; set; }
        public FakeApplication? Application { get; set; }
        public FakeContracts? Contracts { get; set; }
        public FakeCore? Core { get; set; }
        public FakeDashboard? Dashboard { get; set; }
        public FakeData? Data { get; set; }
        public FakeMcp? Mcp { get; set; }
        public FakeNotifications? Notifications { get; set; }
        public FakeProxy? Proxy { get; set; }
        public FakeProxyProtocol? ProxyProtocol { get; set; }
        public FakeProxyProtocolAspNetCore? ProxyProtocolAspNetCore { get; set; }
        public FakeServer? Server { get; set; }
        public FakeStorage? Storage { get; set; }
        public FakeTypedIdsEntityFrameworkCore? TypedIdsEntityFrameworkCore { get; set; }
        public FakeVault? Vault { get; set; }
        public DbContext? Db { get; set; }
        public ILookup<int, int>? Lookup { get; set; }
        public NonLayerType? NotALayer { get; set; }
        public static int CountPositive(int[] values) => values.Count(v => v > 0);
    }
}

namespace Paduraksa.TypedIds.EntityFrameworkCore.ArchFixtures
{
    public sealed class FakeTypedIdsEntityFrameworkCore
    {
        public FakeAcme? Acme { get; set; }
        public FakeAdmin? Admin { get; set; }
        public FakeApplication? Application { get; set; }
        public FakeContracts? Contracts { get; set; }
        public FakeCore? Core { get; set; }
        public FakeDashboard? Dashboard { get; set; }
        public FakeData? Data { get; set; }
        public FakeMcp? Mcp { get; set; }
        public FakeNotifications? Notifications { get; set; }
        public FakeProxy? Proxy { get; set; }
        public FakeProxyProtocol? ProxyProtocol { get; set; }
        public FakeProxyProtocolAspNetCore? ProxyProtocolAspNetCore { get; set; }
        public FakeServer? Server { get; set; }
        public FakeStorage? Storage { get; set; }
        public FakeTypedIds? TypedIds { get; set; }
        public FakeVault? Vault { get; set; }
        public DbContext? Db { get; set; }
        public ILookup<int, int>? Lookup { get; set; }
        public NonLayerType? NotALayer { get; set; }
        public static int CountPositive(int[] values) => values.Count(v => v > 0);
    }
}
