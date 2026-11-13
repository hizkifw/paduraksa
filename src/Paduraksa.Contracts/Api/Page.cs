using System.Text.Json.Serialization;

namespace Paduraksa.Contracts.Api;

/// <summary>One page of a cursor-paginated list.</summary>
/// <typeparam name="T">The item type.</typeparam>
/// <param name="Items">The items, in the order the endpoint documents (newest first for history, ascending by ID for the rest).</param>
/// <param name="NextCursor">An opaque cursor to pass as <c>cursor</c> for the next page; null on the last page. Always written, so a client reads one shape whichever context wrote it.</param>
public sealed record Page<T>(IReadOnlyList<T> Items, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor = null);
