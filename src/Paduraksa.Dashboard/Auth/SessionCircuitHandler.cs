using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Paduraksa.Dashboard.Auth;

/// <summary>
/// Ties a circuit to its session (PLAN §12). When the circuit opens it starts the background re-check; on inbound activity (an event, a
/// navigation, a JavaScript call) it re-checks the session at most every <see cref="DashboardOptions.ActivityRecheckInterval"/>, and an
/// activity that finds the session ended is dropped (the state change sends the page to the sign-in page). Once the session has ended,
/// the circuit gets <see cref="DashboardOptions.EndedCircuitGrace"/> to follow that redirect; any activity after it tears the circuit
/// down, so a client that ignores the redirect cannot keep a circuit alive.
/// </summary>
/// <remarks>
/// Activity checks do not count as use: render acknowledgements and other traffic the browser sends on its own would otherwise keep an
/// unattended page's session from idling out. Service calls, which follow what the person does, count (<see cref="DashboardCaller"/>).
/// </remarks>
internal sealed class SessionCircuitHandler(DashboardAuthenticationStateProvider auth, DashboardOptions options, TimeProvider time) : CircuitHandler
{
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        auth.StartBackgroundRecheck();
        return Task.CompletedTask;
    }

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next) =>
        async context =>
        {
            if (auth.EndedAt is { } ended)
            {
                if (time.GetUtcNow() - ended > options.EndedCircuitGrace)
                {
                    throw new SessionEndedException();
                }

                await next(context).ConfigureAwait(false);
                return;
            }

            if (auth.IsRecheckDue(options.ActivityRecheckInterval)
                && await auth.RecheckAsync(countAsUse: false, CancellationToken.None).ConfigureAwait(false) is null
                && auth.EndedAt is not null)
            {
                return;
            }

            await next(context).ConfigureAwait(false);
        };
}

/// <summary>Thrown into a circuit whose session ended and that is still talking past its grace period: it ends the circuit.</summary>
public sealed class SessionEndedException : Exception
{
    public SessionEndedException()
        : base("The circuit's session has ended.")
    {
    }

    public SessionEndedException(string message)
        : base(message)
    {
    }

    public SessionEndedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
