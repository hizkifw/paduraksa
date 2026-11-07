using System.Reflection;
using static Paduraksa.Tests.Architecture.SolutionLayers;

namespace Paduraksa.Tests.Architecture;

/// <summary>
/// The one exemption from "Vault has no public types" (<see cref="ArchRules.VaultHasNoPublicTypes"/>): <c>dotnet ef</c> emits
/// public migration classes and the repo commits its output verbatim. The exemption is by namespace, so this holds what is in that
/// namespace to schema code: public types there derive from EF's <c>Migration</c> or <c>ModelSnapshot</c> and declare no public
/// member of their own (the generated overrides are protected).
/// </summary>
public sealed class VaultMigrationTests
{
    private const string MigrationsNamespace = "Paduraksa.Vault.Migrations";

    private static readonly string[] SchemaBaseTypes =
    [
        "Microsoft.EntityFrameworkCore.Migrations.Migration",
        "Microsoft.EntityFrameworkCore.Infrastructure.ModelSnapshot",
    ];

    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    [Fact]
    public void PublicTypesInTheVaultMigrationsNamespaceAreSchemaCodeOnly()
    {
        var types = LoadAssembly(Layer.Vault).GetExportedTypes().Where(t => t.Namespace == MigrationsNamespace).ToList();

        Assert.NotEmpty(types);
        var violations = Violations(types);
        Assert.True(violations.Count == 0, string.Join("; ", violations));
    }

    [Fact]
    public void SanityTheCheckFlagsOtherBaseTypesAndPublicMembers()
    {
        var violations = Violations([typeof(NotAMigration), typeof(MigrationWithPublicMember)]);

        Assert.Contains(violations, v => v.Contains(nameof(NotAMigration), StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains(nameof(MigrationWithPublicMember), StringComparison.Ordinal) && v.Contains("Leak", StringComparison.Ordinal));
    }

    private static List<string> Violations(IEnumerable<Type> types)
    {
        var violations = new List<string>();
        foreach (var type in types)
        {
            if (!BaseTypes(type).Any(b => SchemaBaseTypes.Contains(b.FullName)))
            {
                violations.Add($"{type.FullName} derives from neither Migration nor ModelSnapshot");
            }

            // Constructors are fine; anything else public declared on the type itself is not.
            violations.AddRange(type.GetMembers(Declared)
                .Where(m => m is not ConstructorInfo)
                .Select(m => $"{type.FullName} declares public member {m.Name}"));
        }

        return violations;
    }

    private static IEnumerable<Type> BaseTypes(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            yield return current;
        }
    }

    public sealed class NotAMigration;

    public abstract class FakeMigration;

    public sealed class MigrationWithPublicMember : FakeMigration
    {
        public static string Leak() => "x";
    }
}
