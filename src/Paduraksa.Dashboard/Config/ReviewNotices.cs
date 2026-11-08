using Paduraksa.Contracts.Config;

namespace Paduraksa.Dashboard.Config;

/// <summary>
/// The warnings the review drawer shows above Apply (PLAN §12): the compiler's (a host no certificate covers, a binding to a certificate
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
        foreach (HostChange change in changes.OfType<HostChange>())
        {
            HostConfig? old = before.Hosts.FirstOrDefault(h => h.Id == change.HostId);
            HostConfig? @new = after.Hosts.FirstOrDefault(h => h.Id == change.HostId);
            if (old is null)
            {
                continue;
            }

            string title = ChangeView.HostTitle(old);
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

        foreach (UpstreamChange change in changes.OfType<UpstreamChange>().Where(c => c.Kind == ConfigChangeKind.Modified))
        {
            UpstreamConfig? @new = after.Upstreams.FirstOrDefault(u => u.Id == change.UpstreamId);
            UpstreamConfig? old = before.Upstreams.FirstOrDefault(u => u.Id == change.UpstreamId);
            if (old is not null && @new is not null && old.Destinations.Any(d => d.Enabled) && !@new.Destinations.Any(d => d.Enabled))
            {
                notices.Add($"{@new.Name} will have no destination to send requests to.");
            }
        }

        foreach (ConfigWarning warning in warnings)
        {
            notices.Add($"{ChangeView.Locate(warning.Path, after)}: {Sentence(warning.Message)}");
        }

        return notices;
    }

    /// <summary>A message as a sentence: capitalised, with a full stop.</summary>
    public static string Sentence(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string trimmed = message.Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        string capital = char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
        return capital.EndsWith('.') || capital.EndsWith('!') || capital.EndsWith('?') ? capital : capital + ".";
    }
}
