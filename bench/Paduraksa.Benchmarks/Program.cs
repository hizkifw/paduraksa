using System.Runtime;

using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

// Always in-process (`--inProcess` is added unless `--inProcess`/`-i` is given): there is no generated project to
// build (it would inherit the repo's warnings-as-errors and analyzers), and the GC is this project's runtimeconfig:
// workstation, non-concurrent (see the csproj). Any CLI job (`--job short`, `--job dry`, ...) still applies.
// Exits non-zero when a filter matches nothing or any benchmark fails.
if (GCSettings.IsServerGC)
{
    await Console.Error.WriteLineAsync("Refusing to run under Server GC: BenchmarkDotNet's Allocated column is wrong there (spike S3).");
    return 1;
}

bool inProcess = args.Any(static a => a.Equals("--inProcess", StringComparison.OrdinalIgnoreCase) || a == "-i");
string[] arguments = inProcess ? args : [.. args, "--inProcess"];
IConfig config = DefaultConfig.Instance.AddDiagnoser(MemoryDiagnoser.Default);
Summary[] summaries = [.. BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(arguments, config)];

// --list, --help, --info and --version run nothing, legitimately.
bool runsNothing = args.Any(static a => a is "--list" or "--help" or "-h" or "--info" or "--version");
if (summaries.Length == 0 && !runsNothing)
{
    await Console.Error.WriteLineAsync("No benchmark ran: does the filter match anything? (--list flat shows them all)");
    return 2;
}

int failed = summaries.Sum(static s => s.HasCriticalValidationErrors ? 1 : s.Reports.Count(static r => !r.Success));
if (failed > 0)
{
    await Console.Error.WriteLineAsync($"{failed} benchmark(s) or validation(s) failed.");
    return 3;
}

return 0;
