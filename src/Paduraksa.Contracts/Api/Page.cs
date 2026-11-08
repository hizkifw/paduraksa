namespace Paduraksa.Contracts.Api;

/// <summary>One page of a cursor-paginated list.</summary>
/// <typeparam name="T">The item type.</typeparam>
/// <param name="Items">The items, newest first.</param>
/// <param name="NextCursor">An opaque cursor to pass as <c>cursor</c> for the next page; null on the last page.</param>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor = null);
