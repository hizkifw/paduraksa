using System.Text.Json;

using Paduraksa.Dashboard.AboutArea;

namespace Paduraksa.Tests.Dashboard.AboutArea;

/// <summary>
/// The third-party notices list what the server really ships. The truth is the server's <c>paduraksa.deps.json</c> (the runtime packages the SDK keeps
/// after the shared framework's own are taken out; test-only, analyzer and build-only packages never reach it), so a version bump or a new dependency
/// that nobody wrote into the notices fails here.
/// </summary>
public sealed class NoticesCompletenessTests
{
    private const string Fix = "Run scripts/generate-third-party-notices.py and commit THIRD-PARTY-NOTICES.json and THIRD-PARTY-NOTICES.md.";

    [Fact]
    public void EveryShippedPackageIsInTheNoticesAtItsVersion()
    {
        var notices = ThirdPartyNotices.Embedded.Components.Select(component => (component.Name, component.Version)).ToHashSet();

        var missing = ShippedPackages().Where(package => !notices.Contains(package)).Select(package => $"{package.Name} {package.Version}").ToList();

        Assert.True(missing.Count == 0, $"The server ships these, and THIRD-PARTY-NOTICES.json has no entry at that version: {string.Join(", ", missing)}. {Fix}");
    }

    [Fact]
    public void NoNoticeIsForAPackageTheServerNoLongerShips()
    {
        var shipped = ShippedPackages().Select(package => package.Name).ToHashSet(StringComparer.Ordinal);
        var packages = ThirdPartyNotices.Embedded.Components.Where(component => component.Url is not null && component.Version is not null && !IsBundled(component)).ToList();

        var stale = packages.Where(component => !shipped.Contains(component.Name)).Select(component => component.Name).ToList();

        Assert.True(stale.Count == 0, $"THIRD-PARTY-NOTICES.json lists packages the server does not ship: {string.Join(", ", stale)}. {Fix}");
    }

    [Fact]
    public void EveryNoticeHasItsOwnCopyrightLineAndALicenseWithText()
    {
        foreach (var component in ThirdPartyNotices.Embedded.Components)
        {
            Assert.False(string.IsNullOrWhiteSpace(component.Copyright), component.Name + " has no copyright line");
            Assert.False(string.IsNullOrWhiteSpace(ThirdPartyNotices.Embedded.Licenses[component.License].Text), component.Name + "'s license has no text");
        }
    }

    [Fact]
    public void TheRuntimeIsOneEntryThatPointsToItsOwnNotices()
    {
        var runtime = Assert.Single(ThirdPartyNotices.Embedded.Components, component => component.Name.StartsWith(".NET runtime", StringComparison.Ordinal));

        Assert.Equal("MIT", runtime.License);
        Assert.Equal("https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT", runtime.NoticesUrl);
    }

    [Fact]
    public void TheRuntimeEntryIsTheRuntimeTheServerTargets() =>
        Assert.Equal(Environment.Version.ToString(2), Assert.Single(ThirdPartyNotices.Embedded.Components, component => component.Name.StartsWith(".NET runtime", StringComparison.Ordinal)).Version);

    [Fact]
    public void Libmsquic_IsTheVersionTheDockerfileInstalls()
    {
        var pinned = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(Path.Combine(RepoRoot(), "Dockerfile")), @"^ARG LIBMSQUIC_VERSION=(\S+)", System.Text.RegularExpressions.RegexOptions.Multiline);
        Assert.True(pinned.Success, "The Dockerfile has no ARG LIBMSQUIC_VERSION.");

        var listed = Assert.Single(ThirdPartyNotices.Embedded.Components, component => component.Name == "libmsquic");

        Assert.True(listed.Version == pinned.Groups[1].Value, $"The Dockerfile installs libmsquic {pinned.Groups[1].Value} and the notices say {listed.Version}. {Fix}");
    }

    [Fact]
    public void TheGeneratorReproducesTheCheckedInFiles_FromTheServersBuildOutput()
    {
        var python = FindOnPath("python3");
        Assert.SkipWhen(python is null, "python3 is not installed.");
        var output = Path.Combine(Path.GetTempPath(), "paduraksa-notices-" + Environment.ProcessId);
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(python!)
            {
                ArgumentList = { "-I", Path.Combine(RepoRoot(), "scripts", "generate-third-party-notices.py"), "--publish-dir", AppContext.BaseDirectory, "--output-dir", output },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            var errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, "The generator failed: " + errors);

            foreach (var file in new[] { "THIRD-PARTY-NOTICES.json", "THIRD-PARTY-NOTICES.md" })
            {
                Assert.True(
                    File.ReadAllText(Path.Combine(output, file)) == File.ReadAllText(Path.Combine(RepoRoot(), file)),
                    $"{file} is not what the generator writes. {Fix}");
            }
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    private static string? FindOnPath(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, program)).FirstOrDefault(File.Exists);

    [Fact]
    public void TheMarkdownFileListsTheSameComponentsAsTheJson()
    {
        var markdown = File.ReadAllText(Path.Combine(RepoRoot(), "THIRD-PARTY-NOTICES.md"));

        var missing = ThirdPartyNotices.Embedded.Components
            .Select(component => $"## {component.Name}{(component.Version is null ? string.Empty : " " + component.Version)}")
            .Where(heading => !markdown.Contains(heading + "\n", StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0, $"THIRD-PARTY-NOTICES.md lacks {string.Join(", ", missing)}. {Fix}");
    }

    private static bool IsBundled(NoticeComponent component) => component.Name is "SQLite" or "libmsquic" or "Material Design icons" || component.Name.StartsWith(".NET runtime", StringComparison.Ordinal);

    private static IReadOnlyList<(string Name, string Version)> ShippedPackages()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "paduraksa.deps.json");
        Assert.True(File.Exists(path), $"{path} is where the build copies the server's dependency file; the Dashboard test project references the server.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.GetProperty("libraries").EnumerateObject()
            .Where(library => library.Value.GetProperty("type").GetString() == "package")
            .Select(library => library.Name.Split('/'))
            .Select(parts => (parts[0], parts[1]))];
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Paduraksa.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"Paduraksa.slnx not found above {AppContext.BaseDirectory}");
    }
}
