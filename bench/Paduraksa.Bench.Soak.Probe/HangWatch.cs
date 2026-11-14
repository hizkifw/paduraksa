using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Text;

using Microsoft.AspNetCore.Http;

/// <summary>
/// For chasing a request that never finishes (<c>PADURAKSA_SOAK_HANG_FILE</c>, the soak's <c>--watch-hangs</c>): every request in flight is tracked from the hosting
/// layer's begin to its end, with the steps YARP and its upstream client reached, and one that has run longer than <see cref="StuckAfter"/> is written out once,
/// with where it stopped. The first stuck HTTP/2 request also gets a heap dump (<c>createdump</c>) when <c>PADURAKSA_SOAK_HANG_DUMP</c> names a file, for
/// <c>dotnet-dump analyze</c>'s <c>dumpasync</c>. It allocates per request (the hosting events' payloads), so it is off unless asked for.
/// </summary>
internal static partial class HangWatch
{
    // Yama (ptrace_scope 1) lets a process be read only by its ancestors; createdump is a child, so the server names it allowed first.
    private const int PrSetPtracer = 0x59616d61;
    private const ulong PrSetPtracerAny = ulong.MaxValue;

    private static readonly TimeSpan StuckAfter = TimeSpan.FromSeconds(10);
    private static readonly AsyncLocal<Tracked?> Current = new();
    private static readonly ConcurrentDictionary<HttpContext, Tracked> InFlight = new();
    private static string s_file = "";
    private static string? s_dump;
    private static int s_dumped;

    public static void Start(string file, string? dump)
    {
        s_file = file;
        s_dump = string.IsNullOrWhiteSpace(dump) ? null : dump;
        DiagnosticListener.AllListeners.Subscribe(new Listeners());
        _ = new Events();
        Thread thread = new(Watch) { IsBackground = true, Name = "soak-hang-watch" };
        thread.Start();
    }

    private static void Watch()
    {
        while (true)
        {
            Thread.Sleep(1000);
            try
            {
                var now = Stopwatch.GetTimestamp();
                foreach (var tracked in InFlight.Values)
                {
                    if (tracked.Reported || Stopwatch.GetElapsedTime(tracked.Started, now) < StuckAfter || tracked.LongLived)
                    {
                        continue;
                    }

                    tracked.Reported = true;
                    File.AppendAllText(s_file, tracked.Describe(now) + "\n");
                    if (s_dump is not null && tracked.Protocol == "HTTP/2" && Interlocked.Exchange(ref s_dumped, 1) == 0)
                    {
                        Dump(s_dump);
                    }
                }
            }
#pragma warning disable CA1031 // A probe that dies must never take the server with it.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                try
                {
                    File.AppendAllText(s_file, "watch failed: " + ex + "\n");
                }
#pragma warning disable CA1031
                catch (Exception)
#pragma warning restore CA1031
                {
                }
            }
        }
    }

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "prctl")]
    private static partial int Prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);

    private static void Dump(string path)
    {
        _ = Prctl(PrSetPtracer, PrSetPtracerAny, 0, 0, 0);
        var createdump = Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "createdump");
        using var process = Process.Start(new ProcessStartInfo(createdump) { ArgumentList = { "--withheap", "-f", path, Environment.ProcessId.ToString(CultureInfo.InvariantCulture) }, UseShellExecute = false });
        process?.WaitForExit(120_000);
        File.AppendAllText(s_file, $"dump written: {path} (exit {process?.ExitCode})\n");
    }

    private sealed class Tracked(HttpContext context)
    {
        public readonly long Started = Stopwatch.GetTimestamp();
        public readonly DateTime StartedUtc = DateTime.UtcNow;
        public readonly HttpContext Context = context;
        public readonly string Protocol = context.Request.Protocol;
        public readonly string Line = $"{context.Request.Method} {context.Request.Host}{context.Request.Path}{context.Request.QueryString} {context.Request.Protocol} id={context.TraceIdentifier} soak-id={context.Request.Headers["X-Soak-Id"]}";
        public readonly bool LongLived = context.Request.Path.StartsWithSegments("/ws") || context.Request.Path.StartsWithSegments("/upload") && context.Request.Protocol != "HTTP/2";
        private readonly List<(long At, string Step)> _steps = [];
        public bool Reported;

        public void Step(string step)
        {
            lock (_steps)
            {
                _steps.Add((Stopwatch.GetTimestamp(), step));
            }
        }

        public string Describe(long now)
        {
            StringBuilder text = new();
            text.Append(CultureInfo.InvariantCulture, $"{StartedUtc:O} stuck {Stopwatch.GetElapsedTime(Started, now).TotalSeconds:F1} s: {Line}");
            try
            {
                text.Append(CultureInfo.InvariantCulture, $" | response started={Context.Response.HasStarted} status={Context.Response.StatusCode} aborted={Context.RequestAborted.IsCancellationRequested}");
            }
#pragma warning disable CA1031 // The context may be in any state; what can be read is enough.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                text.Append(" | response unreadable: ").Append(ex.GetType().Name);
            }

            text.Append(" | steps:");
            lock (_steps)
            {
                foreach (var (at, step) in _steps)
                {
                    text.Append(CultureInfo.InvariantCulture, $" +{Stopwatch.GetElapsedTime(Started, at).TotalMilliseconds:F2}ms {step};");
                }
            }

            return text.ToString();
        }
    }

    private sealed class Listeners : IObserver<DiagnosticListener>
    {
        public void OnNext(DiagnosticListener value)
        {
            if (value.Name == "Microsoft.AspNetCore")
            {
                value.Subscribe(new Hosting(), static (name, _, _) => name is "Microsoft.AspNetCore.Hosting.BeginRequest" or "Microsoft.AspNetCore.Hosting.EndRequest");
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }

    private sealed class Hosting : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Value?.GetType().GetProperty("httpContext")?.GetValue(value.Value) is not HttpContext context)
            {
                return;
            }

            if (value.Key == "Microsoft.AspNetCore.Hosting.BeginRequest")
            {
                // Set on the request's own flow (Kestrel calls this from its request loop before the app), so the YARP and HttpClient events
                // the request raises later find it.
                Tracked tracked = new(context);
                Current.Value = tracked;
                InFlight[context] = tracked;
            }
            else if (InFlight.TryRemove(context, out var tracked))
            {
                if (tracked.Reported)
                {
                    File.AppendAllText(s_file, $"{DateTime.UtcNow:O} finished after all: {tracked.Line}\n");
                }

                Current.Value = null;
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }

    private sealed class Events : EventListener
    {
        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name is "Yarp.ReverseProxy" or "System.Net.Http")
            {
                EnableEvents(eventSource, EventLevel.Informational);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (Current.Value is not { } tracked || e.EventName is null)
            {
                return;
            }

            var payload = e.Payload is { Count: > 0 } p ? "(" + string.Join(",", p) + ")" : "";
            tracked.Step((e.EventSource.Name == "System.Net.Http" ? "http." : "yarp.") + e.EventName + payload);
        }
    }
}
