using System.Text.RegularExpressions;

using Paduraksa.Fuzz;

namespace Paduraksa.Tests.Fuzz;

/// <summary>The nightly fuzz job runs every target one after another, so a target added without a longer timeout would be cut off at the end of the list, unnoticed.</summary>
public sealed partial class FuzzWorkflowTests
{
    // The harness build, the instrumentation and the upload.
    private const int OverheadMinutes = 20;

    [Fact]
    public void TheNightlyJobsTimeoutFitsEveryTargetForItsTimeBox()
    {
        var workflow = File.ReadAllText(Path.Combine(Corpus.FuzzRoot(), "..", ".github", "workflows", "fuzz.yml"));
        var seconds = int.Parse(Seconds().Match(workflow).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var timeout = int.Parse(FuzzJobTimeout().Match(workflow).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        var needed = (FuzzTargets.All.Count * seconds / 60) + OverheadMinutes;

        Assert.True(timeout >= needed, $"{FuzzTargets.All.Count} targets at {seconds} s need {needed} minutes with the build; fuzz.yml allows {timeout}");
        Assert.True(timeout <= 360, "a GitHub-hosted job is cut off at 360 minutes");
    }

    [GeneratedRegex(@"FUZZ_SECONDS: \$\{\{ github\.event\.inputs\.seconds \|\| '(\d+)' \}\}")]
    private static partial Regex Seconds();

    [GeneratedRegex(@"name: Fuzz the parsers\s+runs-on: [^\n]+\s+timeout-minutes: (\d+)")]
    private static partial Regex FuzzJobTimeout();
}
