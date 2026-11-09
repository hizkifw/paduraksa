namespace Paduraksa.Dashboard.Identity;

/// <summary>
/// The server's bound on concurrent password derivations: sign-in, setup and every re-authentication share a few slots, so PBKDF2
/// can't be used to burn every core. The composition root hands the dashboard the admin app's; without one the dashboard runs
/// derivations unbounded (<see cref="UnboundedPasswordChecks"/>), which only tests do.
/// </summary>
public interface IPasswordCheckSlots
{
    /// <summary>Waits briefly for a slot; the handle gives it back. Null when none freed up in time: refuse without deriving anything.</summary>
    ValueTask<IDisposable?> EnterAsync(CancellationToken cancellationToken);
}

/// <summary>No bound: every check gets a slot at once.</summary>
public sealed class UnboundedPasswordChecks : IPasswordCheckSlots
{
    public ValueTask<IDisposable?> EnterAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IDisposable?>(Released.Instance);

    private sealed class Released : IDisposable
    {
        public static readonly Released Instance = new();

        public void Dispose()
        {
        }
    }
}
