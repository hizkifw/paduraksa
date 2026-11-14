using System.Reflection;
using System.Runtime.CompilerServices;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Paduraksa.Proxy.Http;

/// <summary>
/// Works around a Kestrel HTTP/2 bug that loses the response of a request on a reused (pooled) stream: the app completes, the access log
/// has its 200, and the client never gets a frame (docs/design/hot-path.md "Kestrel's lost HTTP/2 responses"; dotnet/aspnetcore#69690,
/// fixed on main, the 10.0 backport #70100 still open).
/// </summary>
/// <remarks>
/// <para><b>The bug.</b> A stream's output producer is queued to the connection's frame writer by <c>Schedule()</c>, which sets
/// <c>_isScheduled</c> so it is queued once; only the writer clears it, when it takes the producer. A response that used up both its
/// stream's and the connection's send window can wait for the connection's window twice, and the connection's WINDOW_UPDATE then resumes it
/// twice: if the writer is flushing the response's last frame when the second resume comes, the producer is queued once more, and the
/// writer drops that entry because the response is complete, without clearing the flag. The stream goes back to
/// the pool with the flag set (<c>StreamReset</c> doesn't clear it), and on the next request that reuses it every <c>Schedule()</c> returns
/// at once: the response waits in the stream's pipe for good, and the stream never completes, so the connection's teardown never ends
/// either (Kestrel's "application never completed").</para>
/// <para><b>The fix here</b> clears the flag when a request starts on an HTTP/2 stream, before anything of that request can have queued
/// it. Clearing it is safe in every state: a flag set with no entry queued is the bug, and a flag set with an entry still queued only
/// lets one more entry in, which the writer takes and, with nothing new to write, writes nothing.</para>
/// <para><b>Off by itself</b> when the running Kestrel has upstream's fix (the producer has <c>TryUnscheduleCompletedResponse</c>) or
/// doesn't have the fields this reaches (renamed, or a server that isn't Kestrel), so a servicing release retires it without a change
/// here; <c>Http2StreamReuseTests</c> says when it is no longer needed. 0 B: two type checks, a property read and a field reference.</para>
/// </remarks>
public static class KestrelHttp2StreamReuse
{
    private const string KestrelCore = "Microsoft.AspNetCore.Server.Kestrel.Core";
    private const string StreamTypeName = "Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http2.Http2Stream, " + KestrelCore;
    private const string ProtocolTypeName = "Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http.HttpProtocol, " + KestrelCore;
    private const string OutputTypeName = "Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http.IHttpOutputProducer, " + KestrelCore;
    private const string ProducerTypeName = "Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http2.Http2OutputProducer, " + KestrelCore;

    private static readonly Type? StreamType = Type.GetType(StreamTypeName);
    private static readonly Type? ProducerType = Type.GetType(ProducerTypeName);
    private static long s_healed;

    /// <summary>Whether the running Kestrel has the bug and the fields the fix needs.</summary>
    public static bool Applies { get; } = Detect();

    /// <summary>Requests that started on a stream left unable to send, and were put right; for the tests and the soak.</summary>
    public static long Healed => Interlocked.Read(ref s_healed);

    /// <summary>Call at the start of a request, before anything reads or writes its response.</summary>
    public static void BeforeRequest(HttpContext context)
    {
        if (!Applies || !HttpProtocol.IsHttp2(context.Request.Protocol))
        {
            return;
        }

        // Kestrel's stream is the request's own response body feature until a middleware stands in for it.
        var stream = context.Features.Get<IHttpResponseBodyFeature>();
        if (stream is null || !StreamType!.IsInstanceOfType(stream))
        {
            return;
        }

        // An HTTP/2 stream's output is its producer (Http2Stream.Initialize sets it); anything else is left alone.
        var producer = Output(stream);
        if (producer.GetType() != ProducerType)
        {
            return;
        }

        ref var scheduled = ref IsScheduled(producer);
        if (scheduled)
        {
            scheduled = false;
            Interlocked.Increment(ref s_healed);
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Output")]
    [return: UnsafeAccessorType(OutputTypeName)]
    private static extern object Output([UnsafeAccessorType(ProtocolTypeName)] object protocol);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_isScheduled")]
    private static extern ref bool IsScheduled([UnsafeAccessorType(ProducerTypeName)] object producer);

    private static bool Detect()
    {
        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var output = Type.GetType(OutputTypeName);
        return StreamType is not null
            && ProducerType is not null
            && output is not null
            && Type.GetType(ProtocolTypeName)?.GetProperty("Output", Instance)?.PropertyType == output
            && ProducerType.GetField("_isScheduled", Instance)?.FieldType == typeof(bool)
            && ProducerType.GetMethod("TryUnscheduleCompletedResponse", Instance) is null;
    }
}
