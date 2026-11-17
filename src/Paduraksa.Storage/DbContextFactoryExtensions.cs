using Microsoft.EntityFrameworkCore;

namespace Paduraksa.Storage;

/// <summary>Opens a context, runs one unit of work and disposes the context.</summary>
public static class DbContextFactoryExtensions
{
    public static async Task UseAsync<TContext>(this IDbContextFactory<TContext> factory, Func<TContext, Task> work, CancellationToken cancellationToken)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(work);

        var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            await work(context).ConfigureAwait(false);
        }
    }

    public static async Task<TResult> UseAsync<TContext, TResult>(
        this IDbContextFactory<TContext> factory, Func<TContext, Task<TResult>> work, CancellationToken cancellationToken)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(work);

        var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            return await work(context).ConfigureAwait(false);
        }
    }
}
