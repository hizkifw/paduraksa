// Fixtures for the reflection-based rules (typed IDs, Data exposure). NOT production code.
using Paduraksa.Data.ArchFixtures;

namespace Paduraksa.Tests.Architecture.Fixtures;

/// <summary>Stand-in for a generated typed ID (the real ones implement Paduraksa.Core.Ids.ITypedId).</summary>
public readonly record struct FixtureTypedId(Guid Value);

public sealed record UntypedIdDto(Guid Id, string TenantId, long? RouteId, int HostId);

public sealed record TypedIdDto(FixtureTypedId Id, FixtureTypedId? ParentId, string Name, bool IsValid, Guid Token);

public sealed class LeakyDto(FakeData? entity = null)
{
    public FakeData? Entity { get; set; } = entity;

    public IReadOnlyList<FakeData> Entities { get; set; } = [];

    public FakeData[] EntityArray { get; set; } = [];

    public void Accept(FakeData value) => Entity = value;

    public FakeData? Produce() => Entity;

    public Task<FakeData?> ProduceAsync() => Task.FromResult(Entity);

    // Non-public members may use Data types without leaking them.
    internal FakeData? Hidden { get; set; }
}

public sealed record CleanDto(string Name, IReadOnlyList<int> Values);
