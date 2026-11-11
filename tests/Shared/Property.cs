// Linked into Paduraksa.Tests.Proxy and Paduraksa.Tests.Unit (<Compile Include Link>), so it compiles into each test
// assembly as an internal type of its own.
using System.Globalization;
using System.Runtime.CompilerServices;
using CsCheck;

namespace Paduraksa.Tests.Shared;

/// <summary>
/// Property checks over CsCheck that are the same run every time. CsCheck itself seeds only a run's first case and draws
/// the rest at random, which makes a failure in CI a stranger to the next run; here every case's seed is derived from a
/// fixed base, the property's name and the case's number, so a failing case is found again by the same count and a shrunk
/// counterexample is a plain regression test.
/// <para>The cases a property runs are its own baseline times <see cref="ScaleVariable"/> (a decimal, 1 by default), so a
/// local or nightly run goes much deeper without touching a test: <c>PADURAKSA_PROPERTY_SCALE=50 dotnet test</c>.
/// <see cref="SeedVariable"/> changes the base, for a run that wants cases nobody has seen yet.</para>
/// </summary>
internal static class Property
{
    public const string ScaleVariable = "PADURAKSA_PROPERTY_SCALE";

    public const string SeedVariable = "PADURAKSA_PROPERTY_SEED";

    private const int ShrinkIterations = 5_000;

    private const ulong DefaultSeed = 0x9E3779B97F4A7C15;

    private static readonly double Scale = ReadScale();

    private static readonly ulong BaseSeed = ReadSeed();

    /// <summary>The case count of a property whose baseline is <paramref name="baseline"/>, after the environment's scale; at least 1.</summary>
    public static long Cases(long baseline) => Math.Max(1, (long)Math.Round(baseline * Scale));

    /// <summary>
    /// Runs <paramref name="assert"/> on <see cref="Cases"/> inputs of <paramref name="gen"/>. A failure is shrunk and thrown by
    /// CsCheck with the input and the seed that reproduces it (<c>Gen.Sample(..., seed:)</c>).
    /// </summary>
    public static void Check<T>(this Gen<T> gen, long baselineCases, Action<T> assert, [CallerMemberName] string name = "")
    {
        ArgumentNullException.ThrowIfNull(gen);
        ArgumentNullException.ThrowIfNull(assert);
        var stream = Hash(name);
        var cases = Cases(baselineCases);
        for (long i = 0; i < cases; i++)
        {
            var pcg = new PCG(stream, Mix(BaseSeed + (ulong)i));
            var seed = pcg.ToString(pcg.State);
            try
            {
                gen.Sample(assert, seed: seed, iter: 1, threads: 1);
            }
            catch (CsCheckException)
            {
                // CsCheck shrinks by running on from a failing case and keeping the smallest failure it meets, so the failing case is
                // run on with more iterations: that search is random, but it only happens once the property has already failed.
                gen.Sample(assert, seed: seed, iter: ShrinkIterations, threads: 1);
                throw;
            }
        }
    }

    /// <summary>A generator of strings over <paramref name="alphabet"/>; it shrinks toward short strings of the alphabet's first characters.</summary>
    public static Gen<string> StringOf(string alphabet, int minLength, int maxLength) =>
        Gen.Int[0, alphabet.Length - 1].Select(i => alphabet[i]).List[minLength, maxLength].Select(static chars => string.Concat(chars));

    /// <summary>One of <paramref name="items"/>, shrinking toward the first.</summary>
    public static Gen<T> Pick<T>(params T[] items) => Gen.Int[0, items.Length - 1].Select(i => items[i]);

    private static double ReadScale()
    {
        var text = Environment.GetEnvironmentVariable(ScaleVariable);
        if (string.IsNullOrWhiteSpace(text))
        {
            return 1;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) && scale > 0
            ? scale
            : throw new InvalidOperationException($"{ScaleVariable} must be a positive number, got '{text}'.");
    }

    private static ulong ReadSeed()
    {
        var text = Environment.GetEnvironmentVariable(SeedVariable);
        if (string.IsNullOrWhiteSpace(text))
        {
            return DefaultSeed;
        }

        return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seed)
            ? seed
            : throw new InvalidOperationException($"{SeedVariable} must be an unsigned integer, got '{text}'.");
    }

    // FNV-1a: the stream a property draws from depends on its name only, so adding a test moves no other test's cases.
    private static uint Hash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return hash;
    }

    // SplitMix64: neighbouring case numbers give unrelated seeds.
    private static ulong Mix(ulong value)
    {
        value += 0x9E3779B97F4A7C15;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EB;
        return value ^ (value >> 31);
    }
}
