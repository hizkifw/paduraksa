namespace Paduraksa.Mcp;

/// <summary>What a tool call gives back: the JSON of what the function returned, or the message of the error that ended it.</summary>
/// <param name="IsError">The call failed: <paramref name="Text"/> says why and what to change.</param>
/// <param name="Text">The returned value as JSON, or the error message.</param>
/// <param name="Note">Something the model must know about the result: it was cut at the output limit, or requests were still pending when the function returned.</param>
/// <param name="Requests">How many <c>paduraksa.request</c> calls the script made.</param>
public sealed record SandboxOutcome(bool IsError, string Text, string? Note = null, int Requests = 0)
{
    public static SandboxOutcome Error(string message, int requests = 0) => new(true, message, null, requests);
}
