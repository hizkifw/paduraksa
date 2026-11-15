using System.Globalization;
using System.Reflection;

namespace Paduraksa.Core.Hosting;

/// <summary>
/// Which build is running: the product version, the commit it came from and when it was built. <c>GET /api/v1/version</c>, <c>paduraksa doctor</c> and the dashboard's About page
/// all read it here, so they can't disagree about what the assembly says.
/// </summary>
/// <param name="Version">The product version (SemVer), without the commit the SDK appends to it.</param>
/// <param name="Commit">The full SHA the build was given as <c>SourceRevisionId</c>; null for a build that had none.</param>
/// <param name="BuiltAt">When it was built; null when the assembly carries no <see cref="BuildDateKey"/>.</param>
/// <param name="BuiltAtHasTime">False when only the day is known: a local build records the day so that a rebuild within it is still incremental.</param>
public sealed record BuildInfo(string Version, string? Commit, DateTimeOffset? BuiltAt = null, bool BuiltAtHasTime = false)
{
    /// <summary>The <c>AssemblyMetadata</c> key the Server project writes at build time (from <c>SOURCE_DATE_EPOCH</c>, else the day).</summary>
    public const string BuildDateKey = "BuildDate";

    /// <summary>Reads what the SDK wrote into <paramref name="assembly"/>: the informational version, <c>0.1.0+&lt;commit&gt;</c>, and the build date metadata.</summary>
    public static BuildInfo Read(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        var commit = plus >= 0 && plus + 1 < informational.Length ? informational[(plus + 1)..] : null;
        var version = plus >= 0 ? informational[..plus] : informational;

        var date = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == BuildDateKey)?.Value;
        return date is not null && DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var builtAt)
            ? new BuildInfo(version, commit, builtAt, date.Contains('T', StringComparison.Ordinal))
            : new BuildInfo(version, commit);
    }
}
