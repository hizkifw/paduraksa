// Linked into the test projects that read files of the repository (<Compile Include Link>), so it compiles into each assembly as
// an internal type of its own.
namespace Paduraksa.Tests.Shared;

internal static class Repository
{
    /// <summary>The directory holding <c>Paduraksa.slnx</c>, found from the test's base directory.</summary>
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
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
