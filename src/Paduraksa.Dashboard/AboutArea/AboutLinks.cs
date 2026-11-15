namespace Paduraksa.Dashboard.AboutArea;

/// <summary>Where the About page points.</summary>
public static class AboutLinks
{
    public const string Documentation = "https://www.paduraksa.org/";

    public const string Repository = "https://github.com/hizkifw/paduraksa";

    /// <summary>The commit a build came from, on GitHub.</summary>
    public static string Commit(string sha) => $"{Repository}/commit/{sha}";

    /// <summary>
    /// The changelog. Its entries are revisions of the design (Plan r31), not of the product, so there is no entry to link for 0.1.0: the file as it was at
    /// the commit this build came from, or the latest when the build has none.
    /// </summary>
    public static string Changelog(string? commit) => $"{Repository}/blob/{commit ?? "main"}/CHANGELOG.md";

    /// <summary>A short SHA, as Git abbreviates it.</summary>
    public static string ShortSha(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
