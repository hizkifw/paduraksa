using Paduraksa.Core.Vault;

namespace Paduraksa.Vault;

/// <summary>Policy the vault applies; vault policy, not caller options (<see cref="IMacKey"/>).</summary>
/// <param name="MacGraceWindow">
/// How long after a MAC secret is replaced its previous value still verifies. Tokens a proxy issued just before a
/// rotation (forward-auth cookies, PoW challenges) are short-lived, so ten minutes outlasts any of them while keeping the
/// window in which a leaked old key stays useful short. After it the previous value is deleted from the database.
/// </param>
/// <param name="MacCacheMaxAge">
/// How long a MAC handle trusts the key it last read. A replacement or deletion through this process takes effect at once;
/// one made by another process (a CLI) is seen within this time.
/// </param>
internal sealed record VaultPolicy(TimeSpan MacGraceWindow, TimeSpan MacCacheMaxAge)
{
    public static VaultPolicy Default { get; } = new(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(30));
}

/// <summary>
/// What the vault is built from. The public entry point supplies the data directory and the defaults; the rest is the
/// internal seam the vault's own tests substitute (environment, clock, transport), never anything a caller can pass.
/// <c>CreateTerminalHandler</c> builds the handler at the end of an authenticating handler's chain (see
/// <see cref="Secrets.TerminalHandlers"/>).
/// </summary>
internal sealed record VaultOptions(
    string DataDirectory,
    Func<string, string?> GetVariable,
    TimeProvider Time,
    VaultPolicy Policy,
    Func<SecretHttpTransportOptions, HttpMessageHandler> CreateTerminalHandler)
{
    public static VaultOptions For(string dataDirectory) =>
        new(
            Path.GetFullPath(dataDirectory),
            Environment.GetEnvironmentVariable,
            TimeProvider.System,
            VaultPolicy.Default,
            Secrets.TerminalHandlers.CreateSockets);
}
