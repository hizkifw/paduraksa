using Paduraksa.Core.Vault;

namespace Paduraksa.Dashboard.Certificates;

/// <summary>The names a certificate form takes, typed one per line or separated by commas or spaces.</summary>
public static class NameList
{
    private static readonly char[] Separators = [',', ' ', '\n', '\r', '\t', ';'];

    /// <summary>The names in <paramref name="text"/>, trimmed, without empties or repeats (case-insensitive), in the order given.</summary>
    public static IReadOnlyList<string> Parse(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : [.. text.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>A quick check before the service's own: what doesn't look like a DNS name, a <c>*.</c> wildcard or an IP address.</summary>
    public static string? Problem(IReadOnlyList<string> names, bool allowWildcards = true, bool allowIps = true)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0)
        {
            return "Enter at least one name.";
        }

        foreach (var name in names)
        {
            var ip = System.Net.IPAddress.TryParse(name, out _);
            if (ip)
            {
                if (!allowIps)
                {
                    return $"'{name}' is an IP address, which DNS-01 can't prove; use HTTP-01 for it.";
                }

                continue;
            }

            var host = name.StartsWith("*.", StringComparison.Ordinal) ? name[2..] : name;
            if (name.StartsWith("*.", StringComparison.Ordinal) && !allowWildcards)
            {
                return $"'{name}' is a wildcard, which only DNS-01 can prove.";
            }

            if (Uri.CheckHostName(host.TrimEnd('.')) != UriHostNameType.Dns || host.Contains('*', StringComparison.Ordinal))
            {
                return $"'{name}' is not a DNS name, a wildcard or an IP address.";
            }
        }

        return null;
    }

    /// <summary>The key kinds a form offers, with the recommended one first.</summary>
    public static IReadOnlyList<(KeySpec Spec, string Label)> KeySpecs { get; } =
    [
        (KeySpec.EcdsaP256, "ECDSA P-256 (recommended)"),
        (KeySpec.EcdsaP384, "ECDSA P-384"),
        (KeySpec.Rsa2048, "RSA 2048"),
        (KeySpec.Rsa3072, "RSA 3072"),
        (KeySpec.Rsa4096, "RSA 4096"),
    ];
}
