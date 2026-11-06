using Microsoft.AspNetCore.Http;

using Paduraksa.Proxy.Gate;

namespace Paduraksa.Proxy.Snapshot;

/// <summary>What the gate does with a request whose host isn't configured (PLAN §5.1 <c>GlobalSettings</c>).</summary>
public enum DefaultHostAction : byte
{
    /// <summary>Answer with <see cref="DefaultHostBehaviour.Response"/> (a precomputed 404 or 421).</summary>
    Respond,

    /// <summary>Abort without a response: H1 closes the connection, H2/H3 reset the stream.</summary>
    Close,
}

/// <summary>The default-host behaviour of one snapshot.</summary>
public sealed class DefaultHostBehaviour
{
    private DefaultHostBehaviour(DefaultHostAction action, PrecomputedResponse? response)
    {
        Action = action;
        Response = response;
    }

    public DefaultHostAction Action { get; }

    /// <summary>Set when <see cref="Action"/> is <see cref="DefaultHostAction.Respond"/>.</summary>
    public PrecomputedResponse? Response { get; }

    /// <summary>Abort unknown-host requests without answering.</summary>
    public static DefaultHostBehaviour Close { get; } = new(DefaultHostAction.Close, null);

    /// <summary>404 with a short plain-text body.</summary>
    public static DefaultHostBehaviour NotFound(string body = "no host configured\n") =>
        new(DefaultHostAction.Respond, PrecomputedResponse.Text(StatusCodes.Status404NotFound, body));

    /// <summary>421 Misdirected Request: tells H2 clients that coalesced a connection to retry on a new one.</summary>
    public static DefaultHostBehaviour MisdirectedRequest(string body = "misdirected request\n") =>
        new(DefaultHostAction.Respond, PrecomputedResponse.Text(StatusCodes.Status421MisdirectedRequest, body));

    /// <summary>Any other precomputed answer.</summary>
    public static DefaultHostBehaviour RespondWith(PrecomputedResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new(DefaultHostAction.Respond, response);
    }
}
