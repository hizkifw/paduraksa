namespace Paduraksa.Core.Hosting;

/// <summary>
/// What the dashboard's attention item and <c>paduraksa doctor</c> both say about a master key kept beside the vault, so the two can't drift.
/// </summary>
public static class MasterKeyAdvice
{
    public const string BesideTitle = "The master key is stored beside the vault";

    public const string BesideDetail = "A copy of the data directory is a copy of every certificate key and secret in it. Move the key to a Docker secret or a systemd credential, outside the data directory.";

    public const string BesideGuide = "https://www.paduraksa.org/operating/master-key/#moving-the-key-out-of-the-data-directory";
}
