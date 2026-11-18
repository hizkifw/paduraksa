# Paduraksa.TypedIds

Typed IDs for .NET. One declaration per entity generates an ID struct with its own prefix, so a `UserId` can't be passed where an `OrderId` is expected, and every ID reads as its prefix, an underscore and fixed-width base62: `usr_0ZJ3kT9u1h5cQ0wXbN2mLe`.

- **One struct per entity.** A source generator, packed inside this package, writes the struct from a one-line declaration. There is no second package to reference and no version to keep in step.
- **Three ID kinds, chosen per type.** UUIDv7 is the default: time-ordered, so inserts append to an index. UUIDv4 is for public IDs that must not reveal when something was created. Ordered Int64 is for high-volume internal tables, where an 8-byte key matters.
- **Prefixed base62 strings.** The width is fixed, so ordinal string order is ID order, and for the time-ordered kinds that is creation order.
- **Allocation-free text.** Formatting and parsing work over `Span<char>` and UTF-8 bytes without allocating.
- **JSON and `TypeConverter` built in.** An ID is a JSON string, also as a dictionary key, and converts to and from its string form wherever `TypeDescriptor` is used.

Entity Framework Core mapping is in [Paduraksa.TypedIds.EntityFrameworkCore](https://www.nuget.org/packages/Paduraksa.TypedIds.EntityFrameworkCore).

## Declare and use

```csharp
using Paduraksa.TypedIds;

[TypedId("usr")]
public readonly partial record struct UserId;

[TypedId("key", IdKind.UuidV4)]
public readonly partial record struct ApiKeyId;

[TypedId("aud", IdKind.Int64Ordered)]
public readonly partial record struct AuditEventId;
```

```csharp
UserId id = UserId.New();
string text = id.ToString();                       // usr_...

if (UserId.TryParse(text, out UserId parsed)) { }  // also takes ReadOnlySpan<char> and UTF-8 bytes

Span<char> buffer = stackalloc char[UserId.FormattedLength];
id.TryFormat(buffer, out int written, default, null);

string json = JsonSerializer.Serialize(id);        // "usr_..."
```

The prefix is 2 to 8 characters, lowercase letters and digits, starting with a letter. The struct must be a top-level `readonly partial record struct`, and each prefix can be used once per project. The generator reports a mistake as a `PDKID001` to `PDKID006` error.

## ID kinds

| Kind | Value | Use |
|---|---|---|
| `UuidV7` (default) | `Guid`, 22 base62 characters | Most entities. Strictly increasing within a process. |
| `UuidV4` | `Guid`, 22 base62 characters | IDs shown to untrusted clients: 122 random bits, no timestamp. |
| `Int64Ordered` | `long`, 11 base62 characters | Internal append-only tables. |

**Int64 IDs can be enumerated.** Consecutive values differ by small, guessable steps, so never expose one to an untrusted client. They are also unique within one generator only: two processes can issue the same value, and after a restart the clock can be behind the last stored ID. Call `Int64OrderedIdGenerator.Shared.Observe(maxStoredId)` at startup (the EF Core package has a helper that does it for every table).

## Source-generated JSON

A `JsonSerializerContext` doesn't see the converter the generator puts on each ID. Register the factory on it:

```csharp
[JsonSourceGenerationOptions(Converters = [typeof(TypedIdJsonConverterFactory)])]
[JsonSerializable(typeof(Order))]
partial class AppJsonContext : JsonSerializerContext;
```

## Requirements

Targets .NET 10 and .NET 8. The generator needs the C# compiler from the .NET 9.0.300 SDK or later (Roslyn 4.14).
