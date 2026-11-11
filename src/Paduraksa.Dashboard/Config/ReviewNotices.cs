using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;

namespace Paduraksa.Dashboard.Config;

/// <summary>
/// The warnings the review drawer shows above Apply: the compiler's (a host no certificate covers, a binding to a certificate
/// that expired), placed by object name, and what a change does that is easy to miss: a host losing HTTPS, being switched off or removed,
/// or giving up a name.
/// </summary>
public static class ReviewNotices
{
    /// <summary>
    /// The warnings for a change from <paramref name="before"/> to <paramref name="after"/>. The compiler warns about the whole
    /// document, so of its <paramref name="warnings"/> only those the change brings, or that are about an object it touches, are
    /// shown: a host's edit must not repeat an untouched upstream's standing warning (<paramref name="beforeWarnings"/>, those of
    /// <paramref name="before"/>).
    /// </summary>
    public static IReadOnlyList<string> Of(
        ConfigDocument before, ConfigDocument after, IReadOnlyList<ConfigChange> changes, IReadOnlyList<ConfigWarning> warnings,
        IReadOnlyList<ConfigWarning>? beforeWarnings = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(warnings);
        List<string> notices = [];
        foreach (var change in changes.OfType<HostChange>())
        {
            var old = before.Hosts.FirstOrDefault(h => h.Id == change.HostId);
            var @new = after.Hosts.FirstOrDefault(h => h.Id == change.HostId);
            if (old is null)
            {
                continue;
            }

            var title = ChangeView.HostTitle(old);
            if (@new is null)
            {
                if (old.Enabled)
                {
                    notices.Add($"{title} will stop answering: the host is removed, with its routes.");
                }

                continue;
            }

            if (old.Enabled && !@new.Enabled)
            {
                notices.Add($"{title} will stop answering while the host is switched off.");
            }

            if (old.Certificate.Mode != CertificateBindingMode.None && @new.Certificate.Mode == CertificateBindingMode.None)
            {
                notices.Add($"{title} will lose its certificate: HTTPS requests for it are refused (421) once this is live.");
            }

            string[] dropped = [.. old.Names.Except(@new.Names, StringComparer.OrdinalIgnoreCase)];
            if (dropped.Length > 0 && @new.Enabled)
            {
                notices.Add($"{string.Join(", ", dropped)} will no longer be answered by this host.");
            }
        }

        foreach (var change in changes.OfType<UpstreamChange>().Where(c => c.Kind == ConfigChangeKind.Modified))
        {
            var @new = after.Upstreams.FirstOrDefault(u => u.Id == change.UpstreamId);
            var old = before.Upstreams.FirstOrDefault(u => u.Id == change.UpstreamId);
            if (old is not null && @new is not null && old.Destinations.Any(d => d.Enabled) && !@new.Destinations.Any(d => d.Enabled))
            {
                notices.Add($"{@new.Name} will have no destination to send requests to.");
            }
        }

        HashSet<string> touched = [.. changes.Select(ObjectOf)];
        HashSet<(string, string, string)> standing = [.. (beforeWarnings ?? []).Select(w => Key(w, before))];
        foreach (var warning in warnings)
        {
            // A whole-document change touches everything; otherwise a standing warning on an untouched object stays out.
            if (!touched.Contains("*") && standing.Contains(Key(warning, after)) && !touched.Contains(ObjectOf(warning.Path, after)))
            {
                continue;
            }

            notices.Add($"{ChangeView.Locate(warning.Path, after)}: {Sentence(warning.Message)}");
        }

        return notices;
    }

    /// <summary>
    /// A warning by what it says and about which object, not by its path: an object's index can differ between the two documents
    /// (one added or removed before it), its ID can't.
    /// </summary>
    private static (string Object, string Code, string Message) Key(ConfigWarning warning, ConfigDocument document) =>
        (ObjectOf(warning.Path, document), warning.Code, warning.Message);

    private static string ObjectOf(ConfigChange change) => change switch
    {
        HostChange host => $"host:{host.HostId}",
        RouteChange route => $"host:{route.HostId}",
        UpstreamChange upstream => $"upstream:{upstream.UpstreamId}",
        DestinationChange destination => $"upstream:{destination.UpstreamId}",
        ProfileChange profile => $"profile:{profile.ProfileId}",
        RateLimitPolicyChange policy => $"rateLimit:{policy.RateLimitPolicyId}",
        AccessListChange list => $"accessList:{list.AccessListId}",
        RuleSetChange set => $"ruleSet:{set.RuleSetId}",
        SettingsChange => "settings",
        _ => "*",
    };

    /// <summary>The object a document path is about: <c>/hosts/2/routes/0</c> is the host at index 2, by its ID.</summary>
    private static string ObjectOf(string path, ConfigDocument document)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return "document";
        }

        if (segments.Length < 2 || !int.TryParse(segments[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index))
        {
            return segments[0] is "settings" or "defaults" ? "settings" : segments[0];
        }

        return segments[0] switch
        {
            "hosts" when index < document.Hosts.Count => $"host:{document.Hosts[index].Id}",
            "upstreams" when index < document.Upstreams.Count => $"upstream:{document.Upstreams[index].Id}",
            "profiles" when index < document.Profiles.Count => $"profile:{document.Profiles[index].Id}",
            "rateLimitPolicies" when index < document.RateLimitPolicies.Count => $"rateLimit:{document.RateLimitPolicies[index].Id}",
            "accessLists" when index < document.AccessLists.Count => $"accessList:{document.AccessLists[index].Id}",
            "ruleSets" when index < document.RuleSets.Count => $"ruleSet:{document.RuleSets[index].Id}",
            _ => path,
        };
    }

    /// <summary>
    /// A config error as the forms show it. The validator's messages are written for the API and name objects by ID; the few that
    /// are about a choice in a form say it in the form's words instead (a picker left empty, or pointing at something deleted since).
    /// </summary>
    /// <param name="error">The error.</param>
    /// <param name="document">The config the error is about, to name a host the message points at by its path.</param>
    public static string Text(ConfigError error, ConfigDocument? document = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Code switch
        {
            ConfigErrorCodes.HostNamesRequired => "Add at least one hostname.",
            ConfigErrorCodes.HostNameDuplicate when OtherHost(error.Message, document) is { } other =>
                Sentence(error.Message[..error.Message.LastIndexOf(" the enabled host at ", StringComparison.Ordinal)] + $" {other}"),
            ConfigErrorCodes.RouteUpstreamRequired => "Choose an upstream.",
            ConfigErrorCodes.RouteUpstreamUnknown => "That upstream doesn't exist any more. Choose another one.",
            ConfigErrorCodes.HostProfileUnknown => "That profile doesn't exist any more. Choose another one, or no profile.",
            ConfigErrorCodes.HostRateLimitUnknown or ConfigErrorCodes.RouteRateLimitUnknown =>
                "That rate limit doesn't exist any more. Choose another one, or no limit.",
            ConfigErrorCodes.HostAccessListUnknown or ConfigErrorCodes.RouteAccessListUnknown =>
                "That access list doesn't exist any more. Choose another one, or none.",
            ConfigErrorCodes.HostRuleSetUnknown or ConfigErrorCodes.RouteRuleSetUnknown =>
                "That rule set doesn't exist any more. Choose another one, or none.",
            ConfigErrorCodes.RuleEmpty => "State at least one condition (a path, methods or a user agent): a rule with none would match every request.",
            ConfigErrorCodes.AccessListPasswordEmpty or ConfigErrorCodes.AccessListPasswordMissing => "Give this user a password.",
            ConfigErrorCodes.AccessListPasswordTooLong => "That password is too long. Use at most 256 bytes.",
            ConfigErrorCodes.IdEmpty when error.Path.EndsWith("/profileId", StringComparison.Ordinal) => "Choose a profile, or no profile.",
            _ => Sentence(error.Message),
        };
    }

    /// <summary>The host a duplicate-hostname message points at ("... of the enabled host at /hosts/2/names/0"), by its title.</summary>
    private static string? OtherHost(string message, ConfigDocument? document)
    {
        const string Marker = " the enabled host at /hosts/";
        var at = message.LastIndexOf(Marker, StringComparison.Ordinal);
        if (document is null || at < 0)
        {
            return null;
        }

        var rest = message[(at + Marker.Length)..];
        var end = rest.IndexOf('/', StringComparison.Ordinal);
        return int.TryParse(end < 0 ? rest : rest[..end], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
            && index < document.Hosts.Count
                ? ChangeView.HostTitle(document.Hosts[index])
                : null;
    }

    /// <summary>A message as a sentence: capitalised, with a full stop.</summary>
    public static string Sentence(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var trimmed = message.Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        var capital = char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
        return capital.EndsWith('.') || capital.EndsWith('!') || capital.EndsWith('?') ? capital : capital + ".";
    }
}
