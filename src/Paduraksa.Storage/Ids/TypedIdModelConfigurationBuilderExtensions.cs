using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Paduraksa.Storage.Ids;

public static class TypedIdModelConfigurationBuilderExtensions
{
    /// <summary>
    /// Maps every typed ID (<c>ITypedId&lt;TSelf, TValue&gt;</c>) in the model, with no per-property configuration.
    /// Call from <see cref="DbContext.ConfigureConventions"/>; nothing has to be listed or scanned.
    /// </summary>
    /// <remarks>
    /// Uuid columns are 16-byte big-endian <c>BLOB</c>, so SQLite's byte order is time and string order; inside JSON
    /// complex types and primitive collections an ID is its canonical string, so the stored document reads like the
    /// API/export. A property needs a setter of any accessibility, possibly on a base type; computed properties stay
    /// unmapped. Explicit configuration (<c>HasConversion</c>, ...) still wins.
    ///
    /// <b>Hard rule: an ID inside JSON never equals a column ID in SQL</b> (TEXT against BLOB or INTEGER), so comparing
    /// or joining them silently matches nothing. References inside JSON settings are resolved in memory by the snapshot
    /// compiler. In LINQ compare IDs directly: <c>id.Value</c> cannot be translated through a value converter.
    /// </remarks>
    public static ModelConfigurationBuilder AddTypedIdConventions(this ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        configurationBuilder.Conventions.Replace<PropertyDiscoveryConvention>(
            services => new TypedIdPropertyDiscoveryConvention(services.GetRequiredService<ProviderConventionSetBuilderDependencies>()));
        configurationBuilder.Conventions.Add(_ => new TypedIdConvention());
        return configurationBuilder;
    }
}
