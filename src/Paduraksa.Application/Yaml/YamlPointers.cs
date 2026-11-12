namespace Paduraksa.Application.Yaml;

/// <summary>Turns a JSON pointer into the config document (<c>/hosts/0/profileId</c>) into the pointer of the YAML key that writes it (<c>/hosts/0/profile</c>).</summary>
internal static class YamlPointers
{
    public static string FromDocument(string pointer)
    {
        if (pointer.Length == 0)
        {
            return pointer;
        }

        var parts = pointer.Split('/');
        var schema = ConfigYamlSchema.Document;
        for (var i = 1; i < parts.Length && schema is not null; i++)
        {
            if (schema.Kind == SchemaKind.List)
            {
                schema = schema.Element;
            }
            else if (schema.Kind == SchemaKind.Object && schema.Properties.FirstOrDefault(p => p.JsonName == parts[i]) is { } property)
            {
                parts[i] = property.YamlName;
                schema = property.Node;
                continue;
            }
            else
            {
                break;
            }
        }

        return string.Join('/', parts);
    }
}
