using Microsoft.EntityFrameworkCore;
using Paduraksa.Storage;

namespace Paduraksa.Data;

/// <summary>
/// The Storage helper for <see cref="ConfigDbContext"/>: Application can't reference Storage directly, and these keep its calls
/// to <c>factory.UseAsync(context =&gt; ..., ct)</c>.
/// </summary>
public static class ConfigDbContextFactoryExtensions
{
    public static Task UseAsync(this IDbContextFactory<ConfigDbContext> factory, Func<ConfigDbContext, Task> work, CancellationToken cancellationToken) =>
        DbContextFactoryExtensions.UseAsync(factory, work, cancellationToken);

    public static Task<TResult> UseAsync<TResult>(
        this IDbContextFactory<ConfigDbContext> factory, Func<ConfigDbContext, Task<TResult>> work, CancellationToken cancellationToken) =>
        DbContextFactoryExtensions.UseAsync(factory, work, cancellationToken);
}
