using System.Reflection;

using Paduraksa.Application.Commands;

namespace Paduraksa.Tests.Unit.Commands;

/// <summary>
/// A service's constructor takes every dependency the process has, explicitly. An optional parameter lets a composition root forget
/// one and silently run without it (the issuance services once ran with no permission check that way), so none of the Application
/// layer's classes has one; a test says what it leaves out through a shared builder. Records are values, not services.
/// </summary>
public sealed class ApplicationConstructorConventionTests
{
    [Fact]
    public void NoApplicationConstructorHasAnOptionalParameter()
    {
        var offenders = typeof(ConfigService).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && !IsRecord(t) && !IsGenerated(t) && !IsValue(t))
            .SelectMany(t => t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(c => c.GetParameters().Where(p => p.HasDefaultValue).Select(p => $"{t.FullName}({p.ParameterType.Name} {p.Name})")))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    // Exceptions and claims identities carry optional facts (an inner exception, the client's address), not services.
    private static bool IsValue(Type type) => typeof(Exception).IsAssignableFrom(type) || typeof(System.Security.Claims.ClaimsIdentity).IsAssignableFrom(type);

    private static bool IsRecord(Type type) => type.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null;

    private static bool IsGenerated(Type type) => type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false);
}
