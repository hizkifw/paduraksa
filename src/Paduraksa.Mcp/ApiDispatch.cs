namespace Paduraksa.Mcp;

/// <summary>One request a script made, validated and ready to dispatch.</summary>
/// <param name="Method">GET, POST, PUT, PATCH or DELETE.</param>
/// <param name="Path">A plain path under <c>/api/v1</c> that matches an operation of the API description.</param>
/// <param name="Query">The query, one entry per value, not yet encoded.</param>
/// <param name="BodyJson">The JSON body, or <c>null</c>.</param>
/// <param name="IfMatch">The quoted entity tag the change is based on, or <c>null</c>.</param>
public sealed record ApiCall(string Method, string Path, IReadOnlyList<KeyValuePair<string, string>> Query, string? BodyJson, string? IfMatch);

/// <summary>What the API answered.</summary>
/// <param name="Status">The HTTP status.</param>
/// <param name="ContentType">The media type of the body, or <c>null</c> without one.</param>
/// <param name="Body">The body as text (at most <see cref="SandboxLimits.MaxResponseBytes"/>), or <c>null</c>.</param>
/// <param name="ETag">The <c>ETag</c> header, which a later change sends back as <c>ifMatch</c>.</param>
/// <param name="Truncated">The body was longer than the limit and was not read.</param>
public sealed record ApiReply(int Status, string? ContentType, string? Body, string? ETag = null, bool Truncated = false);

/// <summary>Sends a request through the real API pipeline as the token that called the tool; the script never sees the token.</summary>
public interface IApiDispatcher
{
    ValueTask<ApiReply> SendAsync(ApiCall request, CancellationToken cancellationToken);
}
