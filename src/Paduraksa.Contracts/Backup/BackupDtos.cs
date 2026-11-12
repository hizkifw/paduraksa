namespace Paduraksa.Contracts.Backup;

/// <summary>Asks for a full backup, encrypted under a passphrase the archive is useless without.</summary>
/// <param name="Passphrase">At least 12 characters; only you know it, and nothing can recover a lost one.</param>
public sealed record BackupRequest(string Passphrase)
{
    public override string ToString() => $"{nameof(BackupRequest)} {{ *** }}";
}
