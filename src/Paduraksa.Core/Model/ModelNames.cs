namespace Paduraksa.Core.Model;

/// <summary>
/// The stable, lowercase names of the model's enums. These names are what gets persisted (database columns) and
/// hashed (the audit chain), so they are part of the storage format: never rename one, only add new ones. The
/// numeric enum values are never stored.
/// </summary>
public static class ModelNames
{
    public static string ToName(this ActorKind kind) => kind switch
    {
        ActorKind.System => "system",
        ActorKind.Cli => "cli",
        ActorKind.User => "user",
        ActorKind.Token => "token",
        _ => throw Undefined(kind),
    };

    public static string ToName(this Channel channel) => channel switch
    {
        Channel.System => "system",
        Channel.Cli => "cli",
        Channel.Dashboard => "dashboard",
        Channel.Api => "api",
        Channel.Mcp => "mcp",
        _ => throw Undefined(channel),
    };

    public static string ToName(this AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Success => "success",
        AuditOutcome.Denied => "denied",
        AuditOutcome.Invalid => "invalid",
        AuditOutcome.Conflict => "conflict",
        AuditOutcome.Failed => "failed",
        _ => throw Undefined(outcome),
    };

    /// <exception cref="FormatException"><paramref name="name"/> is not one of the names above.</exception>
    public static ActorKind ParseActorKind(string name) => name switch
    {
        "system" => ActorKind.System,
        "cli" => ActorKind.Cli,
        "user" => ActorKind.User,
        "token" => ActorKind.Token,
        _ => throw NotAName<ActorKind>(name),
    };

    /// <inheritdoc cref="ParseActorKind"/>
    public static Channel ParseChannel(string name) => name switch
    {
        "system" => Channel.System,
        "cli" => Channel.Cli,
        "dashboard" => Channel.Dashboard,
        "api" => Channel.Api,
        "mcp" => Channel.Mcp,
        _ => throw NotAName<Channel>(name),
    };

    /// <inheritdoc cref="ParseActorKind"/>
    public static AuditOutcome ParseAuditOutcome(string name) => name switch
    {
        "success" => AuditOutcome.Success,
        "denied" => AuditOutcome.Denied,
        "invalid" => AuditOutcome.Invalid,
        "conflict" => AuditOutcome.Conflict,
        "failed" => AuditOutcome.Failed,
        _ => throw NotAName<AuditOutcome>(name),
    };

    private static FormatException NotAName<T>(string? name)
        where T : struct, Enum =>
        new($"'{name}' is not a {typeof(T).Name} name.");

    private static ArgumentOutOfRangeException Undefined<T>(T value)
        where T : struct, Enum =>
        new(nameof(value), value, $"Undefined {typeof(T).Name}.");
}
