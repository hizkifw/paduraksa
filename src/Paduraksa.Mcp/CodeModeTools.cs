namespace Paduraksa.Mcp;

/// <summary>
/// The two tools of the MCP server (PLAN §11.2), independent of the transport: <c>search</c> runs a script over the API description, <c>execute</c>
/// runs one against the API as the caller. Built once per process from the generated API description; every call is its own engine.
/// </summary>
public sealed class CodeModeTools
{
    private readonly SpecGraph _spec;
    private readonly Action<Exception>? _unexpected;

    private CodeModeTools(SpecGraph spec, RequestPolicy policy, SandboxLimits limits, Action<Exception>? unexpected)
    {
        _spec = spec;
        Policy = policy;
        Limits = limits;
        _unexpected = unexpected;
    }

    public RequestPolicy Policy { get; }

    public SandboxLimits Limits { get; }

    /// <summary>Builds the tools over an OpenAPI document. <paramref name="unexpected"/> receives what the sandbox did not expect (to be logged; the caller is told nothing of it).</summary>
    public static CodeModeTools Create(ReadOnlySpan<byte> openApiJson, SandboxLimits? limits = null, Action<Exception>? unexpected = null)
    {
        var spec = SpecGraph.Parse(openApiJson);
        return new CodeModeTools(spec, RequestPolicy.FromSpec(spec.Root), limits ?? SandboxLimits.Default, unexpected);
    }

    public Task<SandboxOutcome> SearchAsync(string code, CancellationToken cancellationToken) =>
        CodeSandbox.RunAsync(code, new SearchHost(_spec), Limits, _unexpected, cancellationToken);

    public Task<SandboxOutcome> ExecuteAsync(string code, IApiDispatcher dispatcher, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        return CodeSandbox.RunAsync(code, new ExecuteHost(Policy, dispatcher, _unexpected), Limits, _unexpected, cancellationToken);
    }
}
