using System.Text;

using Paduraksa.Mcp;

namespace Paduraksa.Tests.Unit.Mcp;

/// <summary>A small API description with references, a recursive schema and the shapes the policy cares about, and a scripted dispatcher.</summary>
internal static class McpTestSpec
{
    public const string OpenApi = """
        {
          "openapi": "3.1.1",
          "info": { "title": "Test API", "version": "v1" },
          "paths": {
            "/api/v1/hosts": {
              "get": { "operationId": "ListHosts", "summary": "List hosts", "responses": { "200": { "description": "ok", "content": { "application/json": { "schema": { "type": "array", "items": { "$ref": "#/components/schemas/Host" } } } } } } },
              "post": { "operationId": "CreateHost", "parameters": [ { "name": "dryRun", "in": "query", "schema": { "type": "boolean" } } ], "requestBody": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Host" } } } }, "responses": { "201": { "description": "created" } } }
            },
            "/api/v1/hosts/{id}": {
              "get": { "operationId": "GetHost", "parameters": [ { "$ref": "#/components/parameters/Id" } ], "responses": { "200": { "description": "ok" } } },
              "put": { "operationId": "ReplaceHost", "parameters": [ { "$ref": "#/components/parameters/Id" }, { "name": "dryRun", "in": "query", "schema": { "type": "boolean" } } ], "responses": { "200": { "description": "ok" } } },
              "delete": { "operationId": "DeleteHost", "parameters": [ { "$ref": "#/components/parameters/Id" } ], "responses": { "204": { "description": "gone" } } }
            },
            "/api/v1/certificates/{id}/renew": { "post": { "operationId": "RenewCertificate", "responses": { "200": { "description": "ok" } } } },
            "/api/v1/ca": { "get": { "operationId": "GetCa", "responses": { "200": { "description": "ok" } } } },
            "/api/v1/simulate": { "post": { "operationId": "Simulate", "responses": { "200": { "description": "ok" } } } },
            "/api/v1/tokens": { "get": { "operationId": "ListTokens", "responses": { "200": { "description": "ok" } } }, "post": { "operationId": "IssueToken", "responses": { "201": { "description": "ok" } } } },
            "/api/v1/logs/tail": { "get": { "operationId": "StreamAccessLog", "responses": { "200": { "description": "ok" } } } },
            "/api/v1/logs/recent": { "get": { "operationId": "RecentAccessLog", "responses": { "200": { "description": "ok" } } } },
            "/api/v1/dns-providers": { "get": { "operationId": "ListDnsProviders", "responses": { "200": { "description": "ok" } } }, "post": { "operationId": "CreateDnsProvider", "responses": { "201": { "description": "ok" } } } }
          },
          "components": {
            "parameters": { "Id": { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } } },
            "schemas": {
              "Host": { "type": "object", "properties": { "name": { "type": "string" }, "settings": { "$ref": "#/components/schemas/Settings" }, "tree": { "$ref": "#/components/schemas/Node" } } },
              "Settings": { "type": "object", "properties": { "hsts": { "type": "boolean" } } },
              "Node": { "type": "object", "properties": { "children": { "type": "array", "items": { "$ref": "#/components/schemas/Node" } } } }
            },
            "securitySchemes": { "bearer": { "type": "http", "scheme": "bearer" } }
          }
        }
        """;

    public static CodeModeTools Tools(SandboxLimits? limits = null, Action<Exception>? unexpected = null) =>
        CodeModeTools.Create(Encoding.UTF8.GetBytes(OpenApi), limits, unexpected);
}

/// <summary>Answers requests from a function and records them.</summary>
internal sealed class ScriptedDispatcher(Func<ApiCall, ApiReply>? answer = null) : IApiDispatcher
{
    public List<ApiCall> Calls { get; } = [];

    public ValueTask<ApiReply> SendAsync(ApiCall request, CancellationToken cancellationToken)
    {
        lock (Calls)
        {
            Calls.Add(request);
        }

        return ValueTask.FromResult(answer?.Invoke(request) ?? new ApiReply(200, "application/json", "{\"ok\":true}", "\"3\""));
    }
}
