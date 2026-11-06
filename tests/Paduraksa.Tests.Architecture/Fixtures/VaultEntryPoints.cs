// Fixtures for the Vault visibility rule. NOT production code.
using Microsoft.Extensions.DependencyInjection;

namespace Paduraksa.Vault.ArchFixtures;

/// <summary>Allowed: the single public DI-registration entry point shape.</summary>
public static class FixtureVaultServiceCollectionExtensions
{
    public static IServiceCollection AddFixtureVault(this IServiceCollection services) => services;
}

/// <summary>Violation: right name, but not a static class.</summary>
public sealed class NonStaticServiceCollectionExtensions;
