using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Vault.Passwords;

/// <summary>A credential's handle: its current verifier, replaced as a whole when the password changes.</summary>
internal sealed class PasswordCredentialSlot(PasswordCredentialId id) : IPasswordCredential
{
    public PasswordCredentialId Id { get; } = id;

    public volatile ParsedPasswordHash? State;

    public bool IsSet => State is not null;
}
