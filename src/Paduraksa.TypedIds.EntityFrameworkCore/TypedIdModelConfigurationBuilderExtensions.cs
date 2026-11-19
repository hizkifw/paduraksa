using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Paduraksa.TypedIds.EntityFrameworkCore;

/// <summary>Registers the typed-ID conventions on a <see cref="DbContext"/>.</summary>
public static class TypedIdModelConfigurationBuilderExtensions
{
    /// <summary>
    /// Maps every typed ID (<c>ITypedId&lt;TSelf, TValue&gt;</c>) in the model, with no per-property configuration.
    /// Call from <see cref="DbContext.ConfigureConventions"/>; nothing has to be listed or scanned.
    /// </summary>
    /// <remarks>
    /// Uuid columns are 16-byte big-endian binary by default, so the byte order is time and string order on every provider
    /// (<see cref="TypedIdGuidColumns.Native"/> opts into the provider's own Guid column, <c>uuid</c> on PostgreSQL); inside JSON
    /// complex types and primitive collections an ID is its canonical string, so the stored document reads like the
    /// API/export. A property needs a setter of any accessibility, possibly on a base type; computed properties stay
    /// unmapped. Explicit configuration (<c>HasConversion</c>, ...) still wins. The options are fixed per <see cref="DbContext"/> type,
    /// because EF Core builds and caches one model per context type.
    ///
    /// <b>Hard rule: an ID inside JSON never equals a column ID in SQL</b> (text against binary, <c>uuid</c> or integer), so comparing
    /// or joining them silently matches nothing. References inside JSON settings are resolved in memory by the snapshot
    /// compiler. In LINQ compare IDs directly: <c>id.Value</c> cannot be translated through a value converter.
    /// </remarks>
    public static ModelConfigurationBuilder AddTypedIdConventions(
        this ModelConfigurationBuilder configurationBuilder, Action<TypedIdConventionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        TypedIdConventionOptions options = new();
        configure?.Invoke(options);
        configurationBuilder.Conventions.Replace<PropertyDiscoveryConvention>(
            services => new TypedIdPropertyDiscoveryConvention(services.GetRequiredService<ProviderConventionSetBuilderDependencies>()));
        configurationBuilder.Conventions.Add(_ => new TypedIdConvention(options.GuidColumns));
        return configurationBuilder;
    }
}
