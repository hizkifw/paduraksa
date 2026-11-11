namespace Paduraksa.Fuzz;

/// <summary>Runs one input through a target and throws when a property fails: anything that escapes it is a crash to libFuzzer.</summary>
public delegate void FuzzBody(ReadOnlySpan<byte> input);

/// <summary>A byte-level parser the proxy runs on attacker input, with the properties checked on every input.</summary>
public sealed class FuzzTarget(string name, string description, FuzzBody run, Func<IEnumerable<(string Name, byte[] Bytes)>>? seeds = null)
{
    public string Name { get; } = name;

    public string Description { get; } = description;

    /// <summary>The inputs a corpus starts from: well-formed ones, so the fuzzer begins inside the format instead of searching for its magic.</summary>
    public IEnumerable<(string Name, byte[] Bytes)> Seeds() => seeds?.Invoke() ?? [];

    /// <summary>Runs <paramref name="input"/> through the target.</summary>
    public void Run(ReadOnlySpan<byte> input) => run(input);
}
