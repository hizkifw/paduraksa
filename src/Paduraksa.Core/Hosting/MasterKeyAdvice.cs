namespace Paduraksa.Core.Hosting;

/// <summary>
/// What the dashboard's attention item and <c>paduraksa doctor</c> both say about a master key kept beside the vault, so the two can't drift.
/// </summary>
public static class MasterKeyAdvice
{
    public const string BesideTitle = "The master key is stored beside the vault";

    public const string BesideDetail = "A copy of the data directory is a copy of every certificate key and secret in it. Keep the key in a Docker secret or a systemd credential, outside the data directory.";

    public const string BesideGuide = "https://github.com/hizkifw/paduraksa/blob/main/docs/install.md#the-master-key";
}
