namespace Paduraksa.Core.Vault;

/// <summary>
/// The limits a certificate's name list has to fit, shared by everyone who builds one (PLAN §9, §10.1): the vault refuses a
/// certificate with more names than <see cref="MaxNames"/>, so whoever composes a list (the admin listener's own certificate,
/// which adds names of its own) has to stay below it, and both sides read the number from here instead of duplicating it.
/// </summary>
public static class CertificateLimits
{
    /// <summary>The most names one certificate carries; far above what a CA allows (100) and what a homelab needs.</summary>
    public const int MaxNames = 100;
}
