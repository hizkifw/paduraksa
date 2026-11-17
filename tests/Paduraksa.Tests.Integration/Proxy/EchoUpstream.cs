using Microsoft.AspNetCore.Http;

namespace Paduraksa.Tests.Integration.Proxy;

/// <summary>A loopback upstream answering <c>upstream:{path}</c>.</summary>
internal static class EchoUpstream
{
    public static Task<LoopbackUpstream> StartAsync(CancellationToken cancellationToken) =>
        LoopbackUpstream.StartAsync(static context => context.Response.WriteAsync("upstream:" + context.Request.Path), cancellationToken: cancellationToken);
}
