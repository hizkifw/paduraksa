using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

using Paduraksa.Admin.Auth;
using Paduraksa.Mcp;

namespace Paduraksa.Admin.Mcp;

/// <summary>
/// Sends a script's request through the admin app's pipeline as the caller of the MCP request (docs/design/admin-surfaces.md "MCP in code mode (M10)"): a synthetic <see cref="HttpContext"/> that
/// carries the caller's identity, scopes and channel the way authentication set them, never the bearer token, which the code cannot see because it is
/// not in the request. What the caller may do is decided where it is for a REST request: the endpoint's permission filter and the services.
/// </summary>
internal sealed class InProcessApiDispatcher(HttpContext outer, AdminCaller caller, McpPipeline pipeline, IServiceScopeFactory scopes, SandboxLimits limits) : IApiDispatcher
{
    public async ValueTask<ApiReply> SendAsync(ApiCall request, CancellationToken cancellationToken)
    {
        var run = pipeline.Pipeline ?? throw new InvalidOperationException("The admin pipeline has not been built yet.");
        using var scope = scopes.CreateScope();
        DefaultHttpContext context = new() { RequestServices = scope.ServiceProvider, RequestAborted = cancellationToken, TraceIdentifier = outer.TraceIdentifier };
        Fill(context, request);
        context.SetCaller(caller);

        BoundedStream response = new(limits.MaxResponseBytes);
        context.Response.Body = response;
        await run(context).ConfigureAwait(false);
        await context.Response.CompleteAsync().ConfigureAwait(false);

        var etag = context.Response.Headers.ETag.ToString();
        return new ApiReply(
            context.Response.StatusCode,
            context.Response.ContentType,
            response.Length == 0 ? null : response.Text(),
            etag.Length == 0 ? null : etag,
            response.Truncated);
    }

    private void Fill(DefaultHttpContext context, ApiCall call)
    {
        var http = context.Request;
        http.Method = call.Method;
        http.Scheme = outer.Request.Scheme;
        http.Host = outer.Request.Host;
        http.Protocol = "HTTP/1.1";
        http.Path = call.Path;
        http.QueryString = QueryOf(call.Query);
        http.Headers.Accept = "application/json";
        http.Headers.UserAgent = outer.Request.Headers.UserAgent;
        if (call.IfMatch is { } ifMatch)
        {
            http.Headers.IfMatch = ifMatch;
        }

        if (call.BodyJson is { } json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            http.ContentType = "application/json; charset=utf-8";
            http.ContentLength = bytes.Length;
            http.Body = new MemoryStream(bytes, writable: false);
        }

        // The connection as the admin app resolved it for the MCP request (on the internal listener, the forwarded client), so the allowlist, the
        // loopback host check and the audit trail see the same client.
        var connection = context.Connection;
        connection.RemoteIpAddress = outer.Connection.RemoteIpAddress;
        connection.RemotePort = outer.Connection.RemotePort;
        connection.LocalIpAddress = outer.Connection.LocalIpAddress;
        connection.LocalPort = outer.Connection.LocalPort;
        connection.Id = outer.Connection.Id;
    }

    private static QueryString QueryOf(IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        if (pairs.Count == 0)
        {
            return QueryString.Empty;
        }

        var builder = new QueryBuilder(pairs.Select(p => new KeyValuePair<string, StringValues>(p.Key, p.Value)));
        return builder.ToQueryString();
    }

    /// <summary>Keeps what is written up to the limit and counts the rest as not read: a script's request cannot make the server buffer an unbounded answer.</summary>
    private sealed class BoundedStream(int limit) : Stream
    {
        private readonly MemoryStream _kept = new();
        private bool _full;

        public bool Truncated { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _kept.Length;

        public override long Position
        {
            get => _kept.Length;
            set => throw new NotSupportedException();
        }

        public string Text() => Encoding.UTF8.GetString(_kept.GetBuffer(), 0, (int)_kept.Length);

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_full || buffer.Length > limit - (int)_kept.Length)
            {
                // Over the limit the answer is not kept at all: a half of a JSON document is worse than a note that it was too long.
                Truncated = true;
                _full = true;
                _kept.SetLength(0);
                _kept.Capacity = 0;
                return;
            }

            _kept.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
