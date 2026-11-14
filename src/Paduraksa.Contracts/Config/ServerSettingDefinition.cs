using System.Globalization;
using System.Text.Json.Serialization;

using Paduraksa.Core.Json;

namespace Paduraksa.Contracts.Config;

/// <summary>What kind of value a server setting holds, which says how it is written as text and edited.</summary>
[JsonConverter(typeof(ConfigEnumConverter<ServerSettingKind>))]
public enum ServerSettingKind
{
    /// <summary>A time span, written <c>[d.]hh:mm:ss</c>.</summary>
    Duration,

    /// <summary>A whole number (a count, a rate).</summary>
    Number,

    /// <summary>A size in bytes.</summary>
    Size,

    Boolean,

    Text,
}

/// <summary>Where a setting sits on the server settings page.</summary>
[JsonConverter(typeof(ConfigEnumConverter<ServerSettingGroup>))]
public enum ServerSettingGroup
{
    Retention,
    SignIn,
    Notifications,
    Integrations,
    Connections,
}

/// <summary>When a changed value takes effect.</summary>
[JsonConverter(typeof(ConfigEnumConverter<ServerSettingApply>))]
public enum ServerSettingApply
{
    /// <summary>Once the revision is live, with no restart.</summary>
    Live,

    /// <summary>At the next start of the process: the consumer reads it once.</summary>
    Restart,
}

/// <summary>Where the value in force comes from.</summary>
[JsonConverter(typeof(ConfigEnumConverter<ServerSettingSource>))]
public enum ServerSettingSource
{
    /// <summary>Nothing sets it: the built-in default.</summary>
    Default,

    /// <summary>The config's server settings.</summary>
    Config,

    /// <summary>The server's environment or appsettings set it explicitly, which wins over the config.</summary>
    Environment,
}

/// <summary>
/// One server setting: its key in <see cref="ServerSettings"/>, the configuration key the environment may set it with, its bounds and its default.
/// The declarations are in <see cref="ServerSettingCatalog"/>; everything else (the validator, the API, the page, the precedence) walks them.
/// </summary>
public abstract class ServerSettingDefinition
{
    private protected ServerSettingDefinition(
        string key, string configurationKey, ServerSettingGroup group, string section, ServerSettingKind kind, ServerSettingApply apply, string title, string help)
    {
        Key = key;
        ConfigurationKey = configurationKey;
        Group = group;
        Section = section;
        Kind = kind;
        Apply = apply;
        Title = title;
        Help = help;
    }

    /// <summary>The camelCase name of its <see cref="ServerSettings"/> property: its JSON name and its key in the API.</summary>
    public string Key { get; }

    /// <summary>The configuration key under <c>Paduraksa</c> (<c>Metrics:Retention</c>), as appsettings writes it.</summary>
    public string ConfigurationKey { get; }

    /// <summary>The environment variable that sets it (<c>Paduraksa__Metrics__Retention</c>).</summary>
    public string EnvironmentVariable => "Paduraksa__" + ConfigurationKey.Replace(":", "__", StringComparison.Ordinal);

    public ServerSettingGroup Group { get; }

    /// <summary>The heading it sits under within its group (<c>Metrics</c>, <c>Log files</c>).</summary>
    public string Section { get; }

    public ServerSettingKind Kind { get; }

    public ServerSettingApply Apply { get; }

    public string Title { get; }

    public string Help { get; }

    /// <summary>What the review says when a change lowers the value in force (a retention: what is deleted, and that it can't be recovered).</summary>
    public string? LoweredNote { get; init; }

    /// <summary>What the review says about a change from the first settings to the second, when the change needs a word; null says nothing.</summary>
    public Func<ServerSettings, ServerSettings, string?>? Note { get; init; }

    /// <summary>Whether <paramref name="settings"/> sets it.</summary>
    public abstract bool IsSet(ServerSettings settings);

    /// <summary>The value <paramref name="settings"/> sets, as text; null when it sets none.</summary>
    public abstract string? TextOf(ServerSettings settings);

    /// <summary>The value in force for <paramref name="settings"/> (the default where it sets none), as text.</summary>
    public string ResolvedText(ServerSettings settings) => TextOf(settings) ?? DefaultText;

    public abstract string DefaultText { get; }

    public abstract string? MinText { get; }

    public abstract string? MaxText { get; }

    /// <summary>Why the value <paramref name="settings"/> sets can't be used, as a sentence fragment; null when it can (or sets none).</summary>
    public abstract string? Problem(ServerSettings settings);

    /// <summary><paramref name="settings"/> with this setting set from <paramref name="text"/> (null or empty clears it), or why the text doesn't read.</summary>
    public abstract (ServerSettings Settings, string? Problem) WithText(ServerSettings settings, string? text);

    /// <summary><paramref name="target"/> with this setting as <paramref name="source"/> has it (set or not).</summary>
    public abstract ServerSettings CopyFrom(ServerSettings source, ServerSettings target);

    /// <summary>Whether the value in force for <paramref name="after"/> is below the one for <paramref name="before"/> (a shorter retention).</summary>
    public abstract bool Lowered(ServerSettings before, ServerSettings after);

    /// <summary>
    /// A configuration value (appsettings, an environment variable) in the form <see cref="TextOf"/> writes, read the way the configuration binder
    /// reads it (<c>90.00:00:00</c> and <c>90:00:00:00</c> alike); null when it doesn't read, which the options validator has refused already.
    /// </summary>
    public abstract string? FromConfiguration(string raw);

    /// <summary>Whether the value in force differs between the two.</summary>
    public bool Differs(ServerSettings a, ServerSettings b) => !string.Equals(ResolvedText(a), ResolvedText(b), StringComparison.Ordinal);
}

/// <summary>A server setting holding a value type (a duration, a number, a size, a switch).</summary>
public sealed class ServerSetting<T> : ServerSettingDefinition
    where T : struct, IComparable<T>
{
    private readonly Func<ServerSettings, T?> _get;
    private readonly Func<ServerSettings, T?, ServerSettings> _set;

    public ServerSetting(
        string key,
        string configurationKey,
        ServerSettingGroup group,
        string section,
        ServerSettingApply apply,
        string title,
        string help,
        Func<ServerSettings, T?> get,
        Func<ServerSettings, T?, ServerSettings> set,
        T @default,
        T? min = null,
        T? max = null,
        ServerSettingKind? kind = null)
        : base(key, configurationKey, group, section, kind ?? KindOf(), apply, title, help)
    {
        _get = get;
        _set = set;
        Default = @default;
        Min = min;
        Max = max;
    }

    public T Default { get; }

    public T? Min { get; }

    public T? Max { get; }

    public T? Get(ServerSettings settings) => _get(settings);

    /// <summary>The value in force: the one <paramref name="settings"/> sets, else the default.</summary>
    public T Resolve(ServerSettings settings) => _get(settings) ?? Default;

    public ServerSettings Set(ServerSettings settings, T? value) => _set(settings, value);

    public override bool IsSet(ServerSettings settings) => _get(settings) is not null;

    public override string? TextOf(ServerSettings settings) => _get(settings) is { } value ? Format(value) : null;

    public override string DefaultText => Format(Default);

    public override string? MinText => Min is { } min ? Format(min) : null;

    public override string? MaxText => Max is { } max ? Format(max) : null;

    /// <summary>Whether <paramref name="value"/> is inside the bounds.</summary>
    public bool InBounds(T value) => (Min is not { } min || value.CompareTo(min) >= 0) && (Max is not { } max || value.CompareTo(max) <= 0);

    public override string? Problem(ServerSettings settings) =>
        _get(settings) is { } value && !InBounds(value)
            ? $"must be from {ServerSettingText.Describe(Kind, MinText!)} to {ServerSettingText.Describe(Kind, MaxText!)}, was {ServerSettingText.Describe(Kind, Format(value))}"
            : null;

    public override (ServerSettings Settings, string? Problem) WithText(ServerSettings settings, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (_set(settings, null), null);
        }

        return TryParse(text.Trim(), out var value) ? (_set(settings, value), null) : (settings, $"'{text}' isn't {ServerSettingText.KindWord(Kind)}");
    }

    public override ServerSettings CopyFrom(ServerSettings source, ServerSettings target) => _set(target, _get(source));

    public override bool Lowered(ServerSettings before, ServerSettings after) => Resolve(after).CompareTo(Resolve(before)) < 0;

    public override string? FromConfiguration(string raw)
    {
        object? parsed = default(T) switch
        {
            TimeSpan => TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var span) ? span : null,
            long => long.TryParse(raw, NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out var number) ? number : null,
            bool => bool.TryParse(raw.Trim(), out var flag) ? flag : null,
            _ => null,
        };
        return parsed is T typed ? Format(typed) : null;
    }

    /// <summary>Reads the text form of a value (<see cref="Format"/>'s).</summary>
    internal static bool TryParse(string text, out T value)
    {
        object? parsed = default(T) switch
        {
            TimeSpan => TimeSpan.TryParseExact(text, "c", CultureInfo.InvariantCulture, out var span) ? span : null,
            long => long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number : null,
            bool => bool.TryParse(text, out var flag) ? flag : null,
            _ => null,
        };
        value = parsed is T typed ? typed : default;
        return parsed is T;
    }

    /// <summary>The value as the config writes it: invariant, a duration as <c>[d.]hh:mm:ss</c>, a switch as <c>true</c> or <c>false</c>.</summary>
    internal static string Format(T value) => value switch
    {
        TimeSpan span => span.ToString("c", CultureInfo.InvariantCulture),
        long number => number.ToString(CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        _ => throw new NotSupportedException($"A server setting can't hold {typeof(T).Name}."),
    };

    private static ServerSettingKind KindOf() => default(T) switch
    {
        TimeSpan => ServerSettingKind.Duration,
        long => ServerSettingKind.Number,
        bool => ServerSettingKind.Boolean,
        _ => throw new NotSupportedException($"A server setting can't hold {typeof(T).Name}."),
    };
}

/// <summary>A server setting holding text (an address, a name), checked by its own rule.</summary>
public sealed class TextServerSetting : ServerSettingDefinition
{
    private readonly Func<ServerSettings, string?> _get;
    private readonly Func<ServerSettings, string?, ServerSettings> _set;
    private readonly Func<string, string?> _check;

    public TextServerSetting(
        string key,
        string configurationKey,
        ServerSettingGroup group,
        string section,
        ServerSettingApply apply,
        string title,
        string help,
        Func<ServerSettings, string?> get,
        Func<ServerSettings, string?, ServerSettings> set,
        string @default,
        Func<string, string?> check)
        : base(key, configurationKey, group, section, ServerSettingKind.Text, apply, title, help)
    {
        _get = get;
        _set = set;
        _check = check;
        Default = @default;
    }

    public string Default { get; }

    /// <summary>What its rule accepts, in words, for the guide's table.</summary>
    public string Accepted { get; init; } = "text";

    public string? Get(ServerSettings settings) => _get(settings);

    public string Resolve(ServerSettings settings) => _get(settings) ?? Default;

    public ServerSettings Set(ServerSettings settings, string? value) => _set(settings, value);

    /// <summary>Why <paramref name="value"/> can't be used; null when it can.</summary>
    public string? Check(string value) => _check(value);

    public override bool IsSet(ServerSettings settings) => _get(settings) is not null;

    public override string? TextOf(ServerSettings settings) => _get(settings);

    public override string DefaultText => Default;

    public override string? MinText => null;

    public override string? MaxText => null;

    public override string? Problem(ServerSettings settings) => _get(settings) is { } value ? _check(value) : null;

    // Text is the one kind where empty is a value (no address): only null clears it.
    public override (ServerSettings Settings, string? Problem) WithText(ServerSettings settings, string? text) => (_set(settings, text?.Trim()), null);

    public override ServerSettings CopyFrom(ServerSettings source, ServerSettings target) => _set(target, _get(source));

    public override bool Lowered(ServerSettings before, ServerSettings after) => false;

    public override string? FromConfiguration(string raw) => raw.Trim();
}
