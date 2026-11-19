using Microsoft.EntityFrameworkCore;
using Paduraksa.Tests.TypedIdSample;
using Paduraksa.TypedIds.EntityFrameworkCore;

namespace Paduraksa.TypedIds.Tests;

// One context type per setting: EF caches a model per context type, which is also how a real application fixes the setting.
// They map the sample entities, so the keys, foreign keys, JSON complex type and primitive collection are all in the model.

internal sealed class NativeGuidContext(DbContextOptions<NativeGuidContext> options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.AddTypedIdConventions(options => options.GuidColumns = TypedIdGuidColumns.Native);

    protected override void OnModelCreating(ModelBuilder modelBuilder) => NativeGuidModel.Configure(modelBuilder);
}

internal sealed class BinaryGuidContext(DbContextOptions<BinaryGuidContext> options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.AddTypedIdConventions(options => options.GuidColumns = TypedIdGuidColumns.Binary);

    protected override void OnModelCreating(ModelBuilder modelBuilder) => NativeGuidModel.Configure(modelBuilder);
}

internal static class NativeGuidModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Host>(host =>
        {
            host.HasOne<Host>().WithMany().HasForeignKey(h => h.ParentId);
            host.ComplexProperty(h => h.Settings, settings =>
            {
                settings.ToJson();
                settings.ComplexCollection(s => s.Aliases);
            });
        });
        modelBuilder.Entity<Route>().ComplexProperty(r => r.Origin);
        modelBuilder.Entity<Challenge>().HasOne<Host>().WithMany().HasForeignKey(c => c.HostId);
        modelBuilder.Entity<AuditEvent>().HasOne<AuditEvent>().WithOne().HasForeignKey<AuditEvent>(a => a.PreviousId);
    }
}
