using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Protocol;

using Paduraksa.Admin.Auth;
using Paduraksa.Admin.OpenApi;
using Paduraksa.Mcp;

namespace Paduraksa.Admin.Mcp;

/// <summary>
/// The two tools of the MCP endpoint over the transport (docs/design/admin-surfaces.md "MCP in code mode (M10)"): lists them, and runs a call as the caller authentication set on the request.
/// A failure of any kind is a tool result with <c>isError</c>, never an exception into the MCP host.
/// </summary>
internal sealed partial class McpToolkit(OpenApiDocumentCache document, McpPipeline pipeline, IServiceScopeFactory scopes, IHttpContextAccessor accessor, ILogger<McpToolkit> logger, AdminMcpOptions options, McpSwitch? mcpSwitch = null)
    : IAsyncDisposable
{
    private static readonly JsonElement CodeSchema = JsonDocument.Parse(
        $$$"""{"type":"object","properties":{"code":{"type":"string","description":"{{{ToolDescriptions.CodeDescription}}}"}},"required":["code"],"additionalProperties":false}""").RootElement.Clone();

    private readonly Lock _gate = new();
    private Task<CodeModeTools>? _tools;

    public static ListToolsResult List()
    {
        var limits = SandboxLimits.Default;
        return new ListToolsResult
        {
            Tools =
            [
                new Tool
                {
                    Name = ToolDescriptions.SearchName,
                    Title = "Search the API description",
                    Description = ToolDescriptions.Search(limits),
                    InputSchema = CodeSchema,
                    Annotations = new ToolAnnotations { ReadOnlyHint = true, DestructiveHint = false, IdempotentHint = true, OpenWorldHint = false },
                },
                new Tool
                {
                    Name = ToolDescriptions.ExecuteName,
                    Title = "Call the admin API",
                    Description = ToolDescriptions.Execute(limits),
                    InputSchema = CodeSchema,
                    Annotations = new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = true, IdempotentHint = false, OpenWorldHint = false },
                },
            ],
        };
    }

    public async Task<CallToolResult> CallAsync(CallToolRequestParams request, CancellationToken cancellationToken)
    {
        try
        {
            if (request.Name is not (ToolDescriptions.SearchName or ToolDescriptions.ExecuteName))
            {
                return Failure($"There is no tool '{request.Name}'; the tools are {ToolDescriptions.SearchName} and {ToolDescriptions.ExecuteName}.");
            }

            if (request.Arguments is null || !request.Arguments.TryGetValue("code", out var code) || code.ValueKind != JsonValueKind.String || code.GetString() is not { } text)
            {
                return Failure("The argument 'code' is required and must be a string: a JavaScript async arrow function.");
            }

            var tools = await ToolsAsync().ConfigureAwait(false);
            SandboxOutcome outcome;
            if (request.Name == ToolDescriptions.SearchName)
            {
                outcome = await tools.SearchAsync(text, cancellationToken).ConfigureAwait(false);
            }
            else if (accessor.HttpContext is { } http && http.GetCaller() is { } caller)
            {
                InProcessApiDispatcher dispatcher = new(http, caller, pipeline, scopes, tools.Limits);
                outcome = await tools.ExecuteAsync(text, dispatcher, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                return Failure("The request carries no authenticated caller.");
            }

            List<ContentBlock> content = [new TextContentBlock { Text = outcome.Text }];
            if (outcome.Note is { } note)
            {
                content.Add(new TextContentBlock { Text = note });
            }

            return new CallToolResult { IsError = outcome.IsError, Content = content };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogToolFailed(logger, ex, request.Name);
            return Failure("The tool failed on the server. The details are in the server log.");
        }
    }

    private static CallToolResult Failure(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    /// <summary>Built once per process from the generated API description; a failed build is not kept, so the next call tries again.</summary>
    private Task<CodeModeTools> ToolsAsync()
    {
        lock (_gate)
        {
            if (_tools is null || _tools.IsFaulted)
            {
                _tools = BuildAsync();
            }

            return _tools;
        }
    }

    private async Task<CodeModeTools> BuildAsync()
    {
        var openApi = await document.GetAsync(CancellationToken.None).ConfigureAwait(false);
        return CodeModeTools.Create(openApi, options.ChildCommand, mcpSwitch?.IdleTimeout ?? options.IdleTimeout, log: message => LogSandbox(logger, message));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The MCP tool {Tool} failed.")]
    private static partial void LogToolFailed(ILogger logger, Exception exception, string? tool);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP sandbox: {Event}")]
    private static partial void LogSandbox(ILogger logger, string @event);

    /// <summary>The children this server's pool has running; none before the first call. Other servers in the same process have their own.</summary>
    internal IReadOnlyList<int> ChildProcessIds
    {
        get
        {
            Task<CodeModeTools>? tools;
            lock (_gate)
            {
                tools = _tools;
            }

            return tools is { IsCompletedSuccessfully: true } ? tools.Result.Pool.ChildProcessIds : [];
        }
    }

    /// <summary>
    /// Stops the sandbox pool and kills its children (MCP switched off, or its idle timeout changed): the next call, once allowed, builds a new one
    /// with the settings then in force. A call running now finishes or fails with the pool.
    /// </summary>
    public async Task StopAsync()
    {
        Task<CodeModeTools>? tools;
        lock (_gate)
        {
            tools = _tools;
            _tools = null;
        }

        if (tools is not null)
        {
            try
            {
                await (await tools.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogStopFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stopping the MCP sandbox pool failed; its children are killed with the process.")]
    private static partial void LogStopFailed(ILogger logger, Exception exception);

    /// <summary>Stopping the app kills every child: none outlives the server.</summary>
    public async ValueTask DisposeAsync()
    {
        Task<CodeModeTools>? tools;
        lock (_gate)
        {
            tools = _tools;
        }

        if (tools is { IsCompletedSuccessfully: true })
        {
            await tools.Result.DisposeAsync().ConfigureAwait(false);
        }
    }
}
