using System.Text;

namespace Paduraksa.Mcp;

/// <summary>
/// The two tools of the MCP server (PLAN §11.2), independent of the transport: <c>search</c> runs a script over the API description, <c>execute</c> runs one
/// against the API as the caller. Both run in a child process of the pool; the request policy is applied here, in the parent.
/// </summary>
public sealed class CodeModeTools : IAsyncDisposable
{
    private readonly SandboxProcessPool _pool;

    private CodeModeTools(SandboxProcessPool pool, RequestPolicy policy, SandboxLimits limits)
    {
        _pool = pool;
        Policy = policy;
        Limits = limits;
    }

    public RequestPolicy Policy { get; }

    public SandboxLimits Limits { get; }

    public SandboxProcessPool Pool => _pool;

    /// <summary>Builds the tools over an OpenAPI document. <paramref name="log"/> receives the pool's one-line events (a kill, a despawn, what the hardening could not apply).</summary>
    public static CodeModeTools Create(
        ReadOnlySpan<byte> openApiJson, SandboxChildCommand command, TimeSpan idleTimeout, SandboxLimits? limits = null, TimeProvider? time = null, Action<string>? log = null)
    {
        limits ??= SandboxLimits.Default;
        var spec = SpecGraph.Parse(openApiJson);
        SandboxProcessPool pool = new(command, limits, idleTimeout, Encoding.UTF8.GetString(openApiJson), time, log);
        return new CodeModeTools(pool, RequestPolicy.FromSpec(spec.Root), limits);
    }

    public Task<SandboxOutcome> SearchAsync(string code, CancellationToken cancellationToken) =>
        _pool.RunAsync("search", code, Policy, NoDispatcher.Instance, cancellationToken);

    public Task<SandboxOutcome> ExecuteAsync(string code, IApiDispatcher dispatcher, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        return _pool.RunAsync("execute", code, Policy, dispatcher, cancellationToken);
    }

    public ValueTask DisposeAsync() => _pool.DisposeAsync();

    /// <summary>A search has no <c>paduraksa</c> global; if its child asked for a request anyway, the answer is a failure.</summary>
    private sealed class NoDispatcher : IApiDispatcher
    {
        public static NoDispatcher Instance { get; } = new();

        public ValueTask<ApiReply> SendAsync(ApiCall request, CancellationToken cancellationToken) => throw new InvalidOperationException("A search sends no requests.");
    }
}
