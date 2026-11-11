using Paduraksa.Fuzz;

using SharpFuzz;

// Paduraksa.Fuzz list
// Paduraksa.Fuzz replay [target...]        every committed input through its target, no libFuzzer (what CI runs)
// Paduraksa.Fuzz seed                      writes the targets' seed inputs into fuzz/corpus
// PADURAKSA_FUZZ_TARGET=<target> libfuzzer-dotnet --target_path=Paduraksa.Fuzz ...   under libFuzzer, on assemblies instrumented with sharpfuzz
return Run(args);

static int Run(string[] args)
{
    switch (args.Length > 0 ? args[0] : null)
    {
        case "list":
            foreach (var target in FuzzTargets.All)
            {
                Console.WriteLine($"{target.Name}\t{target.Description}");
            }

            return 0;
        case "replay":
            var (replayed, failures) = Corpus.Replay(Corpus.FuzzRoot(), args.Length > 1 ? args[1..] : null);
            Console.WriteLine($"{replayed} inputs replayed, {failures.Count} failed");
            foreach (var failure in failures)
            {
                Console.Error.WriteLine(failure);
            }

            return failures.Count == 0 ? 0 : 1;
        case "seed":
            Console.WriteLine($"{Corpus.WriteSeeds(Corpus.FuzzRoot())} seed inputs written");
            return 0;
        case null:
        case "libfuzzer":
            var name = args.Length > 1 ? args[1] : Environment.GetEnvironmentVariable("PADURAKSA_FUZZ_TARGET");
            if (string.IsNullOrEmpty(name))
            {
                Console.Error.WriteLine("Name the target in PADURAKSA_FUZZ_TARGET (or as the argument after 'libfuzzer'); `list` shows them.");
                return 2;
            }

            // The target is found inside the first run, not before it: instrumented code may only run once SharpFuzz has set up its shared memory,
            // and the targets' statics (an address parsed, a challenge minted) are instrumented code.
            FuzzTarget? chosen = null;
            Fuzzer.LibFuzzer.Run(input => (chosen ??= FuzzTargets.Get(name)).Run(input));
            return 0;
        default:
            Console.Error.WriteLine("Unknown mode '" + args[0] + "': list, replay, seed or libfuzzer.");
            return 2;
    }
}
