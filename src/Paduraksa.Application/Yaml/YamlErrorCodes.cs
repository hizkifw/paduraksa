namespace Paduraksa.Application.Yaml;

/// <summary>The stable <see cref="Contracts.Config.ConfigError.Code"/>s of the YAML import, which come before the config validator's own (<see cref="Config.ConfigErrorCodes"/>).</summary>
public static class YamlErrorCodes
{
    // The text itself.
    public const string TooLarge = "yaml.tooLarge";
    public const string Encoding = "yaml.encoding";
    public const string Syntax = "yaml.syntax";
    public const string Documents = "yaml.documents";
    public const string Tag = "yaml.tag";
    public const string Depth = "yaml.depth";
    public const string Nodes = "yaml.nodes";
    public const string ScalarTooLong = "yaml.scalar.tooLong";
    public const string KeyInvalid = "yaml.key.invalid";
    public const string KeyDuplicate = "yaml.key.duplicate";
    public const string AnchorLimit = "yaml.anchor.limit";
    public const string AliasLimit = "yaml.alias.limit";
    public const string AliasUnknown = "yaml.alias.unknown";

    // The shape against the config's schema.
    public const string Header = "yaml.header";
    public const string KeyUnknown = "yaml.key.unknown";
    public const string KeyRequired = "yaml.key.required";
    public const string TypeMismatch = "yaml.type.mismatch";
    public const string ValueInvalid = "yaml.value.invalid";

    // Names, IDs and what they match.
    public const string ReferenceUnknown = "yaml.reference.unknown";
    public const string ReferenceAmbiguous = "yaml.reference.ambiguous";
    public const string IdInvalid = "yaml.id.invalid";
    public const string Duplicate = "yaml.duplicate";
    public const string CredentialUnknown = "yaml.credential.unknown";
}
