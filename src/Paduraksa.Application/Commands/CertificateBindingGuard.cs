using Paduraksa.Application.Compilation;
using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;

namespace Paduraksa.Application.Commands;

/// <summary>Refuses certificate bindings a command would introduce that can't be used.</summary>
internal static class CertificateBindingGuard
{
    /// <summary>
    /// The errors for certificate bindings that <paramref name="candidate"/> introduces and that can't be used:
    /// a host whose Specific binding is new or changed, or a changed default certificate, naming a certificate that is
    /// missing, expired, pending, failed or without a key. A binding the current config already has is left alone, so a
    /// certificate that expired since doesn't block unrelated changes (it stays a warning).
    /// </summary>
    public static List<ConfigError> IntroducedUnusable(ConfigDocument current, ConfigDocument candidate,
        IReadOnlyList<ConfigWarning> warnings)
    {
        List<ConfigError> errors = [];
        foreach (var warning in warnings)
        {
            if (warning.Code == ConfigWarningCodes.CertificateDefaultUnusable)
            {
                if (candidate.Settings.DefaultCertificateId != current.Settings.DefaultCertificateId)
                {
                    errors.Add(new ConfigError(warning.Path, ConfigErrorCodes.SettingsDefaultCertificateUnusable,
                        warning.Message));
                }
            }
            else if (warning.Code == ConfigWarningCodes.CertificateSpecificUnusable
                     && CommandErrors.HostIndexOf(warning.Path) is { } index && index < candidate.Hosts.Count)
            {
                var host = candidate.Hosts[index];
                if (current.Hosts.FirstOrDefault(h => h.Id == host.Id)?.Certificate != host.Certificate)
                {
                    errors.Add(new ConfigError(warning.Path, ConfigErrorCodes.HostCertificateUnusable,
                        warning.Message));
                }
            }
        }

        return errors;
    }
}
