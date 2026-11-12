namespace Paduraksa.Application.Yaml;

/// <summary>A YAML node with its position in the text (1-based), for errors. The tree is what the reader builds and the mapping reads; nothing in it is typed yet.</summary>
internal abstract class YamlNode(long line, long column)
{
    public long Line { get; } = line;

    public long Column { get; } = column;
}

/// <summary>A scalar. <see cref="Plain"/> scalars are the only ones whose text the schema may read as another type (<c>null</c>, <c>true</c>, a number); a quoted or block scalar is always text.</summary>
internal sealed class YamlScalar(string text, bool plain, long line, long column) : YamlNode(line, column)
{
    public string Text { get; } = text;

    public bool Plain { get; } = plain;
}

internal sealed class YamlSequence(long line, long column) : YamlNode(line, column)
{
    public List<YamlNode> Items { get; } = [];
}

internal sealed class YamlMapping(long line, long column) : YamlNode(line, column)
{
    public List<YamlEntry> Entries { get; } = [];

    public YamlEntry? Find(string key)
    {
        foreach (var entry in Entries)
        {
            if (string.Equals(entry.Key, key, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }
}

internal sealed class YamlEntry(string key, long keyLine, long keyColumn, YamlNode value)
{
    public string Key { get; } = key;

    public long KeyLine { get; } = keyLine;

    public long KeyColumn { get; } = keyColumn;

    public YamlNode Value { get; set; } = value;
}
