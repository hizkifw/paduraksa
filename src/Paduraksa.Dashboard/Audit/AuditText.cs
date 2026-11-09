using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

using Paduraksa.Contracts.Audit;
using Paduraksa.Contracts.Revisions;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Config;

namespace Paduraksa.Dashboard.Audit;

/// <summary>How the audit page reads an event: the action in words and its area, the outcome's tone, who acted, where a target links to.</summary>
public static class AuditText
{
    private static readonly FrozenDictionary<string, string> Actions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["auth.login"] = "Signed in",
        ["auth.second_factor"] = "Second sign-in step",
        ["auth.lockout"] = "Account locked",
        ["auth.logout"] = "Signed out",
        ["auth.session.revoke"] = "Ended a session",
        ["auth.session.revoke_others"] = "Signed out other sessions",
        ["token.issue"] = "Issued an API token",
        ["token.revoke"] = "Revoked an API token",
        ["user.create"] = "Added a user",
        ["user.update"] = "Changed a user",
        ["user.delete"] = "Deleted a user",
        ["user.enable"] = "Enabled a user",
        ["user.disable"] = "Disabled a user",
        ["user.password.change"] = "Changed their password",
        ["user.password.reset"] = "Reset a password",
        ["user.totp.enroll"] = "Began two-step setup",
        ["user.totp.confirm"] = "Turned on two-step verification",
        ["user.totp.disable"] = "Turned off two-step verification",
        ["user.recovery_codes.regenerate"] = "Made new recovery codes",
        ["setup.complete"] = "Finished first-run setup",
        ["config.host.upsert"] = "Saved a host",
        ["config.host.delete"] = "Deleted a host",
        ["config.upstream.upsert"] = "Saved an upstream",
        ["config.upstream.delete"] = "Deleted an upstream",
        ["config.profile.upsert"] = "Saved a profile",
        ["config.profile.delete"] = "Deleted a profile",
        ["config.settings.update"] = "Changed the global settings",
        ["config.replace"] = "Replaced the config",
        ["config.rollback"] = "Rolled the config back",
        ["config.publish"] = "Published the config",
        ["certificate.add"] = "Added a certificate",
        ["certificate.update"] = "Changed a certificate",
        ["certificate.delete"] = "Deleted a certificate",
        ["acme.account.create"] = "Added an ACME account",
        ["acme.account.deactivate"] = "Deactivated an ACME account",
        ["acme.dns_provider.create"] = "Added a DNS provider",
        ["acme.dns_provider.delete"] = "Deleted a DNS provider",
        ["acme.dns_provider.rotate"] = "Replaced a DNS provider's credential",
        ["acme.dns_provider.test"] = "Tested a DNS provider",
        ["acme.order.request"] = "Requested a certificate",
        ["acme.order.renew"] = "Renewed a certificate",
        ["acme.order.issued"] = "A certificate was issued",
        ["acme.order.failed"] = "A certificate order failed",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The action filters offered as suggestions: each area with <c>*</c>, then the actions in words.</summary>
    public static IReadOnlyList<string> Suggestions { get; } =
        [.. new[] { "auth.*", "user.*", "token.*", "config.*", "certificate.*", "acme.*", "setup.*" }.Concat(Actions.Keys.Order(StringComparer.Ordinal))];

    /// <summary>The action in words; the dotted name, spelt out, for one this page doesn't know.</summary>
    public static string Describe(string action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Actions.TryGetValue(action, out string? words))
        {
            return words;
        }

        string[] parts = action.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? action : FieldLabels.Of(string.Join(' ', parts.Skip(1).DefaultIfEmpty(parts[0])).Replace('_', ' '));
    }

    /// <summary>The area of an action (its first segment), for the chip's colour.</summary>
    public static Tone AreaTone(string action) => action.Split('.')[0] switch
    {
        "auth" or "setup" => Tone.Info,
        "user" or "token" => Tone.Primary,
        "config" => Tone.Success,
        "certificate" or "acme" => Tone.Warning,
        _ => Tone.Default,
    };

    public static string Name(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Success => "Success",
        AuditOutcome.Denied => "Denied",
        AuditOutcome.Invalid => "Invalid",
        AuditOutcome.Conflict => "Conflict",
        _ => "Failed",
    };

    public static Tone ToneOf(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Success => Tone.Success,
        AuditOutcome.Denied => Tone.Error,
        AuditOutcome.Failed => Tone.Error,
        _ => Tone.Warning,
    };

    /// <summary>
    /// Who acted: "You", a user's display name or a token's name when the page could read them, "Paduraksa" or "The CLI"; otherwise the
    /// kind with a shortened ID.
    /// </summary>
    public static string Actor(ActorRef actor, UserId? me, IReadOnlyDictionary<UserId, string> users, IReadOnlyDictionary<ApiTokenId, string> tokens)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(tokens);
        return actor.Kind switch
        {
            ActorKind.User when actor.User == me => "You",
            ActorKind.User when actor.User is { } user && users.TryGetValue(user, out string? name) => name,
            ActorKind.User when actor.User is { } user => $"User {Identity.IdentityText.ShortId(user.ToString())}",
            ActorKind.Token when actor.Token is { } token && tokens.TryGetValue(token, out string? name) => $"Token \"{name}\"",
            ActorKind.Token when actor.Token is { } token => $"Token {Identity.IdentityText.ShortId(token.ToString())}",
            ActorKind.Cli => "The CLI",
            _ => "Paduraksa",
        };
    }

    /// <summary>Where a target links to in the dashboard, when it is something with a page: a host, an upstream, a profile, a revision.</summary>
    public static string? LinkOf(string? target)
    {
        if (target is null)
        {
            return null;
        }

        if (HostId.TryParse(target, out HostId host))
        {
            return DashboardRoutes.Host(host);
        }

        if (UpstreamId.TryParse(target, out UpstreamId upstream))
        {
            return DashboardRoutes.Upstream(upstream);
        }

        if (ProfileId.TryParse(target, out ProfileId profile))
        {
            return DashboardRoutes.Profile(profile);
        }

        return ConfigRevisionId.TryParse(target, out ConfigRevisionId revision) ? DashboardRoutes.Revision(revision) : null;
    }

    /// <summary>The details, indented, for the expanded row.</summary>
    public static string Pretty(JsonElement details) =>
        JsonSerializer.Serialize(details, PrettyOptions);

    private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };

    /// <summary>
    /// Writes one event as a line of JSON Lines: the fields the API returns, enums and actors by their names, the details as stored. The
    /// details never hold a secret, so neither does the export.
    /// </summary>
    public static void WriteLine(IBufferWriter<byte> output, AuditEventSummary e)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(e);
        using (Utf8JsonWriter json = new(output))
        {
            json.WriteStartObject();
            json.WriteString("id", e.Id.ToString());
            json.WriteString("timestamp", e.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            json.WriteStartObject("actor");
            json.WriteString("kind", e.Actor.Kind.ToName());
            if (e.Actor.User is { } user)
            {
                json.WriteString("user", user.ToString());
            }

            if (e.Actor.Token is { } token)
            {
                json.WriteString("token", token.ToString());
            }

            json.WriteEndObject();
            json.WriteString("channel", e.Channel.ToName());
            json.WriteString("action", e.Action);
            json.WriteString("outcome", e.Outcome.ToName());
            if (e.Target is { } target)
            {
                json.WriteString("target", target);
            }

            if (e.ClientIp is { } ip)
            {
                json.WriteString("clientIp", ip);
            }

            if (e.Details is { } details)
            {
                json.WritePropertyName("details");
                details.WriteTo(json);
            }

            if (e.RevisionId is { } revision)
            {
                json.WriteString("revisionId", revision.ToString());
            }

            json.WriteEndObject();
        }

        output.Write("\n"u8);
    }
}
