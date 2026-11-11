namespace Paduraksa.Fuzz;

/// <summary>The committed inputs of every target and what replaying them means.</summary>
public static class Corpus
{
    /// <summary>The directory of the corpus of each target, one folder per target.</summary>
    public const string CorpusFolder = "corpus";

    /// <summary>Inputs that once crashed a target: kept beside the corpus, replayed with it, and each also a unit test where the bug lives.</summary>
    public const string RegressionFolder = "regressions";

    /// <summary>The <c>fuzz</c> folder of the repository, found from <paramref name="start"/> (or the running assembly) by walking up to the solution file.</summary>
    public static string FuzzRoot(string? start = null)
    {
        var directory = new DirectoryInfo(start ?? AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Paduraksa.slnx")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new DirectoryNotFoundException("No Paduraksa.slnx above " + (start ?? AppContext.BaseDirectory) + "; pass the fuzz folder explicitly.")
            : Path.Combine(directory.FullName, "fuzz");
    }

    /// <summary>Every committed input of <paramref name="target"/>: its corpus and its regressions.</summary>
    public static IEnumerable<string> Files(string fuzzRoot, FuzzTarget target)
    {
        foreach (var folder in (string[])[CorpusFolder, RegressionFolder])
        {
            var directory = Path.Combine(fuzzRoot, folder, target.Name);
            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>
    /// Runs every committed input of the named targets (all of them when none is named) through its target. No libFuzzer and no instrumented assembly: this is the
    /// check that a corpus input, or a crash that was fixed, still passes.
    /// </summary>
    /// <returns>The inputs replayed, and a line for each one that failed.</returns>
    public static (int Replayed, IReadOnlyList<string> Failures) Replay(string fuzzRoot, IReadOnlyCollection<string>? targets = null)
    {
        var replayed = 0;
        List<string> failures = [];
        foreach (var target in FuzzTargets.All)
        {
            if (targets is { Count: > 0 } && !targets.Contains(target.Name))
            {
                continue;
            }

            foreach (var file in Files(fuzzRoot, target))
            {
                replayed++;
                try
                {
                    target.Run(File.ReadAllBytes(file));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    failures.Add($"{target.Name} {Path.GetRelativePath(fuzzRoot, file)}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        return (replayed, failures);
    }

    /// <summary>Writes the seeds of every target that are not in its corpus folder yet; returns how many were written.</summary>
    public static int WriteSeeds(string fuzzRoot)
    {
        var written = 0;
        foreach (var target in FuzzTargets.All)
        {
            var directory = Path.Combine(fuzzRoot, CorpusFolder, target.Name);
            Directory.CreateDirectory(directory);
            foreach (var (name, bytes) in target.Seeds())
            {
                var path = Path.Combine(directory, "seed-" + name);
                if (!File.Exists(path))
                {
                    File.WriteAllBytes(path, bytes);
                    written++;
                }
            }
        }

        return written;
    }
}
