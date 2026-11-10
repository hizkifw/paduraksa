using Paduraksa.Core.Model;

namespace Paduraksa.Application.ProofOfWork;

/// <summary>
/// The HMAC key the proof-of-work gate signs tokens and challenges with: one vault secret of kind
/// <see cref="Core.Vault.SecretKind.HmacSigningKey"/> and algorithm HMAC-SHA-256, under an ID that is the same in every
/// process so a restart finds the same key.
/// </summary>
/// <remarks>
/// Like the admin certificate's, the ID is a fixed value a random <see cref="SecretId.New"/> practically never reproduces,
/// so no admin-created secret collides with it. The process owns its lifecycle: it is created at startup when missing,
/// never deleted, and rotated only through the vault's <c>ReplaceAsync</c> (which keeps the ID and lets the previous value
/// verify for the vault's grace window).
/// </remarks>
public static class ProofOfWorkKey
{
    /// <summary>The ID the key is stored under, stable across processes.</summary>
    public static SecretId Id { get; } = SecretId.FromValue(new Guid("0190a000-0000-7000-8000-706f776b6579"));

    /// <summary>The label the vault shows for it.</summary>
    public const string Label = "Proof-of-work token key";
}
