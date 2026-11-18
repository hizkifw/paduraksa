# Paduraksa.TypedIds.EntityFrameworkCore

Entity Framework Core mapping for [Paduraksa.TypedIds](https://www.nuget.org/packages/Paduraksa.TypedIds). One model convention maps every typed ID in the model, with nothing to list per property or per entity.

- **One convention for every ID.** UUID-based IDs are stored as a 16-byte big-endian binary column, and ordered Int64 IDs as an integer. A key, a foreign key, a nullable ID and a collection of IDs all map the same way.
- **Order is kept.** The binary form is big-endian, so a column sorts the way the string form does, and for the time-ordered kinds that is creation order. `ORDER BY` and the index agree.
- **Readable in JSON.** Inside a JSON complex type or a primitive collection, an ID is its string form, so a stored document reads like the API output.
- **Int64 seeding.** `ObserveInt64OrderedKeysAsync` reads the largest key of every table keyed by an ordered Int64 ID and feeds it to the generator at startup, so new IDs stay above the stored ones when the clock is behind.
- **Explicit configuration wins.** A `HasConversion` on a property replaces the convention for that property.

Provider-neutral: it needs `Microsoft.EntityFrameworkCore.Relational` and no particular database. Tested on SQLite. Targets .NET 10 (EF Core 10).

## Use

```csharp
using Paduraksa.TypedIds;
using Paduraksa.TypedIds.EntityFrameworkCore;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.AddTypedIdConventions();
}

public sealed class User
{
    public UserId Id { get; set; } = UserId.New();
    public string Name { get; set; } = "";
}
```

At startup, once, before anything creates IDs:

```csharp
await using var context = await factory.CreateDbContextAsync();
await context.ObserveInt64OrderedKeysAsync(Int64OrderedIdGenerator.Shared);
```

## Queries

Compare IDs directly: `db.Users.Where(u => u.Id == id)`. `id.Value` can't be translated through the converter. An ID inside a JSON document is text and a column ID is binary or an integer, so comparing or joining the two matches nothing: resolve references that live in JSON in memory.

A property needs a setter of any accessibility, possibly on a base type. A property without one, such as `UserId Other => Id`, stays unmapped. Typed IDs inside an owned type mapped with `OwnsOne(...).ToJson()` are rejected; use `ComplexProperty(...).ToJson()`.
