using Paduraksa.Fuzz;

namespace Paduraksa.Tests.Fuzz;

/// <summary>
/// The committed fuzz inputs (the corpus of each target and the inputs that once crashed one) through the targets, with no libFuzzer and no instrumented assembly, on
/// every commit. A corpus input that starts to fail, or a fixed crash that comes back, fails here.
/// </summary>
public sealed class CorpusReplayTests
{
    public static TheoryData<string> Targets => [.. FuzzTargets.All.Select(t => t.Name)];

    [Theory]
    [MemberData(nameof(Targets))]
    public void EveryCommittedInputPassesItsTarget(string name)
    {
        var (replayed, failures) = Corpus.Replay(Corpus.FuzzRoot(), [name]);

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Assert.True(replayed > 0, $"{name} has no corpus: `Paduraksa.Fuzz seed` writes its seeds, and a fuzz run grows them");
    }

    [Fact]
    public void EveryCorpusFolderIsATarget()
    {
        var root = Corpus.FuzzRoot();
        string[] known = [.. FuzzTargets.All.Select(t => t.Name)];
        foreach (var folder in (string[])[Corpus.CorpusFolder, Corpus.RegressionFolder])
        {
            var directory = Path.Combine(root, folder);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var found in Directory.EnumerateDirectories(directory).Select(Path.GetFileName))
            {
                Assert.Contains(found, known);
            }
        }
    }
}
