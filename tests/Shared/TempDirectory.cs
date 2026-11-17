// Linked into the test projects that need a scratch directory (<Compile Include Link>), so it compiles into each assembly as an
// internal type of its own.
namespace Paduraksa.Tests.Shared;

/// <summary>A fresh, empty directory under the system temp path, deleted again on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("paduraksa-tests-").FullName;

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
