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
    /// <summary>The warnings for a change from <paramref name="before"/> to <paramref name="after"/>.</summary>
    public static IReadOnlyList<string> Of(ConfigDocument before, ConfigDocument after, IReadOnlyList<ConfigChange> changes, IReadOnlyList<ConfigWarning> warnings)
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

        foreach (var warning in warnings)
        {
            notices.Add($"{ChangeView.Locate(warning.Path, after)}: {Sentence(warning.Message)}");
        }

        return notices;
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
