namespace Paduraksa.Tests.Integration;

/// <summary>A fresh, empty directory under the system temp path, deleted again on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("paduraksa-tests-").FullName;

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
