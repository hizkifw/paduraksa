namespace Paduraksa.Application.Hosting;

/// <summary>
/// A signal the process gives background work that needs its listeners: completed once every listener is bound, so the
/// renewal scheduler never starts an HTTP-01 validation before the public listener can answer it. Implemented by the
/// server's health state; a process that registers issuance without one (unit tests, tools) is ready from the start.
/// </summary>
public interface IProcessReadiness
{
    /// <summary>Completes when the listeners are bound; cancels with <paramref name="cancellationToken"/> if the process stops first.</summary>
    Task WaitUntilReadyAsync(CancellationToken cancellationToken);
}
