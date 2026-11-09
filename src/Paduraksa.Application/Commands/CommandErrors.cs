using System.Globalization;

using Paduraksa.Contracts.Config;

namespace Paduraksa.Application.Commands;

/// <summary>
/// Turns document errors into what an object command reports (<see cref="Contracts.Commands.ConfigCommandResult"/>):
/// errors inside the object the caller sent, relative to it, and the rest, pointing into the canonical document.
/// </summary>
internal static class CommandErrors
{
    /// <summary>The top-level lists whose elements are objects with IDs.</summary>
    private static readonly string[] ObjectLists = ["profiles", "upstreams", "hosts"];

    /// <summary>
    /// Splits <paramref name="errors"/>: those at or below <paramref name="ownPath"/> (the sent object's pointer in the
    /// document that produced them; null when there is none, as for a delete) become relative to it; the others get
    /// their top-level object index mapped through <paramref name="toCanonical"/> (null: already canonical).
    /// </summary>
    public static (List<ConfigError> Own, List<ConfigError> Related) Split(
        IReadOnlyList<ConfigError> errors, string? ownPath, IndexMap? toCanonical)
    {
        ArgumentNullException.ThrowIfNull(errors);
        List<ConfigError> own = [];
        List<ConfigError> related = [];
        foreach (ConfigError error in errors)
        {
            if (ownPath is not null && IsAtOrBelow(error.Path, ownPath))
            {
                own.Add(error with { Path = error.Path[ownPath.Length..] });
            }
            else
            {
                related.Add(toCanonical is null ? error : error with { Path = toCanonical.Remap(error.Path) });
            }
        }

        return (own, related);
    }

    /// <summary>The pointer of the object with ID <paramref name="id"/> in <paramref name="list"/> of
    /// <paramref name="document"/> (<c>/hosts/3</c>), or null when it isn't there.</summary>
    public static string? PathOf(ConfigDocument document, string list, string id)
    {
        int index = IdsOf(document, list).IndexOf(id);
        return index < 0 ? null : $"/{list}/{index.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>The host index of a pointer at or below <c>/hosts/{i}</c>; null for any other pointer.</summary>
    public static int? HostIndexOf(string path)
    {
        string[] parts = path.Split('/', 4);
        return parts.Length >= 3 && parts[0].Length == 0 && parts[1] == "hosts"
            && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            ? index
            : null;
    }

    private static bool IsAtOrBelow(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.Ordinal) && (path.Length == prefix.Length || path[prefix.Length] == '/');

    private static List<string> IdsOf(ConfigDocument document, string list) => list switch
    {
        "profiles" => [.. (document.Profiles ?? []).Select(p => p?.Id.ToString() ?? string.Empty)],
        "upstreams" => [.. (document.Upstreams ?? []).Select(u => u?.Id.ToString() ?? string.Empty)],
        "hosts" => [.. (document.Hosts ?? []).Select(h => h?.Id.ToString() ?? string.Empty)],
        _ => throw new ArgumentOutOfRangeException(nameof(list), list, "Not an object list."),
    };

    /// <summary>Maps the top-level object index of a pointer (<c>/hosts/<b>3</b>/routes/0</c>) from one arrangement
    /// of a document to another, by object ID.</summary>
    internal sealed class IndexMap
    {
        private readonly Dictionary<string, Dictionary<int, int>> _byList = new(StringComparer.Ordinal);

        private IndexMap()
        {
        }

        /// <summary>The map from <paramref name="from"/>'s indices to <paramref name="to"/>'s.</summary>
        public static IndexMap Between(ConfigDocument from, ConfigDocument to)
        {
            IndexMap map = new();
            foreach (string list in ObjectLists)
            {
                List<string> target = IdsOf(to, list);
                Dictionary<string, int> positions = new(StringComparer.Ordinal);
                for (int i = 0; i < target.Count; i++)
                {
                    positions.TryAdd(target[i], i);
                }

                List<string> source = IdsOf(from, list);
                Dictionary<int, int> indices = [];
                for (int i = 0; i < source.Count; i++)
                {
                    if (positions.TryGetValue(source[i], out int j))
                    {
                        indices[i] = j;
                    }
                }

                map._byList[list] = indices;
            }

            return map;
        }

        /// <summary><paramref name="pointer"/> with its top-level index mapped; unchanged when there is none to map.</summary>
        public string Remap(string pointer)
        {
            // "/hosts/3/...": list token, index token, rest.
            string[] parts = pointer.Split('/', 4);
            if (parts.Length < 3
                || parts[0].Length != 0
                || !_byList.TryGetValue(parts[1], out Dictionary<int, int>? indices)
                || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                || !indices.TryGetValue(index, out int mapped))
            {
                return pointer;
            }

            string head = $"/{parts[1]}/{mapped.ToString(CultureInfo.InvariantCulture)}";
            return parts.Length == 4 ? $"{head}/{parts[3]}" : head;
        }
    }
}
