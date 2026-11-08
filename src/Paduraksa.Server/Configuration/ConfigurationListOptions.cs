
using Microsoft.Extensions.Configuration;

namespace Paduraksa.Server.Configuration;

/// <summary>
/// Binds the list options a user sets from the environment in <b>both</b> forms the configuration binder and a shell offer:
/// indexed keys (<c>Paduraksa__Listen__AdminAllowedSources__0=…</c>) and one comma-separated scalar
/// (<c>Paduraksa__Listen__AdminAllowedSources=192.168.1.0/24,10.0.0.0/8</c>). The binder silently ignores a scalar for an
/// <see cref="IList{T}"/>, which would leave a documented setting doing nothing — for the admin allowlist that means the
/// admin is open while the operator believes it is restricted. A scalar is split on commas, each entry trimmed and empty
/// entries dropped.
/// </summary>
/// <remarks>
/// The normalisation runs once, after binding and before validation, so the validator and the services read the same
/// values. The one shape it cannot honour is both forms at once: the scalar wins deterministically and a startup warning
/// says that the indexed entries were ignored, so nothing is silently dropped.
/// </remarks>
internal static class ConfigurationListOptions
{
    private static readonly (string Path, Func<PaduraksaOptions, IList<string>> Get)[] Lists =
    [
        ("Listen:AdminAllowedSources", static o => o.Listen.AdminAllowedSources),
        ("Listen:HttpProxyProtocol:TrustedSources", static o => o.Listen.HttpProxyProtocol.TrustedSources),
        ("Listen:HttpsProxyProtocol:TrustedSources", static o => o.Listen.HttpsProxyProtocol.TrustedSources),
        ("Admin:CertificateNames", static o => o.Admin.CertificateNames),
        ("Acme:DnsPropagation:Resolvers", static o => o.Acme.DnsPropagation.Resolvers),
    ];

    /// <summary>Normalises every list option in place and records a conflicting shape in <see cref="PaduraksaOptions.ListBindingWarnings"/>.</summary>
    public static void Bind(IConfiguration configuration, PaduraksaOptions options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        foreach ((string path, Func<PaduraksaOptions, IList<string>> get) in Lists)
        {
            string key = $"{PaduraksaOptions.SectionName}:{path}";
            IConfigurationSection section = configuration.GetSection(key);
            List<IConfigurationSection> children = [.. section.GetChildren()];
            string? scalar = section.Value;

            // A section with children reports an empty Value, not null: only a non-empty scalar is the scalar form. An
            // explicit empty scalar with no children still clears the list (AdminAllowedSources= means "no restriction").
            bool scalarForm = !string.IsNullOrEmpty(scalar) || (scalar is not null && children.Count == 0);
            if (!scalarForm)
            {
                continue;
            }

            if (children.Count > 0)
            {
                options.ListBindingWarnings.Add(
                    $"{key} is set both as a scalar and with indexed keys; the scalar value is used and the indexed entries are ignored.");
            }

            IList<string> target = get(options);
            target.Clear();
            foreach (string entry in Split(scalar!))
            {
                target.Add(entry);
            }
        }
    }

    /// <summary>Comma-separated, trimmed, empty entries dropped. Kept visible for the tests and for the docs.</summary>
    internal static IReadOnlyList<string> Split(string scalar) =>
        [.. scalar.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
}
