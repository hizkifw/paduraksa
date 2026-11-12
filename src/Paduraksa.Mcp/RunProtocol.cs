using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Paduraksa.Mcp;

/// <summary>The child broke a limit or the protocol: the parent kills it and tells the caller <see cref="Reason"/>.</summary>
internal sealed class RunBreachException(string reason, int requests) : Exception(reason)
{
    public string Reason { get; } = reason;

    public int Requests { get; } = requests;
}

/// <summary>What one run has used so far, as the watchdog and the protocol both see it.</summary>
internal sealed class RunState(string runId)
{
    /// <summary>
    /// Fresh and random for every run, sent in the <c>run</c> frame and required in every frame the child answers it with. A frame that carries another one, or none,
    /// is not an answer to this run: a result the child had ready before the code arrived, a request it queued while idle, a replay of an earlier run's.
    /// </summary>
    public string RunId { get; } = runId;

    public RunClock Clock { get; } = new();

    public int Requests { get; set; }
}

/// <summary>Wall time of a run that is not spent waiting for the parent's own answer to a request.</summary>
internal sealed class RunClock
{
    private readonly Lock _gate = new();
    private long _since = Stopwatch.GetTimestamp();
    private TimeSpan _accumulated;
    private bool _running = true;

    public TimeSpan Active
    {
        get
        {
            lock (_gate)
            {
                return _running ? _accumulated + Stopwatch.GetElapsedTime(_since) : _accumulated;
            }
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            _accumulated += Stopwatch.GetElapsedTime(_since);
            _running = false;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            _since = Stopwatch.GetTimestamp();
            _running = true;
        }
    }
}

/// <summary>
/// The parent's half of one run over a pair of streams (PLAN §11.2): read the child's frames, answer its requests through the <see cref="RequestBridge"/>,
/// and end with its result. Everything the child sends is checked here; the child is not trusted to be a child. It is a function of streams so that a
/// hostile peer can be fed to it directly (the fuzz target does).
/// </summary>
internal static class RunProtocol
{
    public sealed record Exchange(SandboxOutcome Outcome, bool Recycle);

    public const int RunIdMaxChars = 64;

    public static byte[] RunFrame(string tool, string code, SandboxLimits limits, string runId) => Messages.Build(w =>
    {
        w.WriteString("t", "run");
        w.WriteString("rid", runId);
        w.WriteString("tool", tool);
        w.WriteString("code", code);
        w.WriteStartObject("limits");
        w.WriteBoolean("constraints", limits.EngineConstraints);
        w.WriteNumber("statements", limits.EngineMaxStatements);
        w.WriteNumber("memoryMiB", (int)(limits.EngineMemoryBytes / (1024 * 1024)));
        w.WriteNumber("depth", limits.EngineCallDepth);
        w.WriteNumber("output", limits.MaxOutputBytes);
        w.WriteEndObject();
    });

    /// <exception cref="RunBreachException">The child asked for more requests than allowed, or its stream ended.</exception>
    /// <exception cref="SandboxProtocolException">The child sent something the protocol does not allow.</exception>
    public static async Task<Exchange> ExchangeAsync(
        Stream fromChild, Stream toChild, RequestBridge bridge, SandboxLimits limits, RunState state, Action<string> log, CancellationToken cancellationToken)
    {
        while (true)
        {
            var frame = await FrameCodec.ReadAsync(fromChild, FrameCodec.MaxFromChild, cancellationToken).ConfigureAwait(false)
                ?? throw new RunBreachException("the sandbox process ended", state.Requests);
            using var message = Messages.Parse(frame);
            var type = Messages.TypeOf(message);
            if (type is "request" or "result" && !string.Equals(Messages.Text(message.RootElement, "rid", RunIdMaxChars, required: false), state.RunId, StringComparison.Ordinal))
            {
                throw new SandboxProtocolException("A frame is not an answer to this run.");
            }

            switch (type)
            {
                case "request":
                    if (++state.Requests > limits.MaxRequests)
                    {
                        throw new RunBreachException($"it made more than {limits.MaxRequests} requests", limits.MaxRequests);
                    }

                    await AnswerAsync(message.RootElement, toChild, bridge, limits, state, log, cancellationToken).ConfigureAwait(false);
                    break;
                case "result":
                    return new Exchange(Result(message.RootElement, limits, state.Requests), Messages.Flag(message.RootElement, "recycle"));
                default:
                    throw new SandboxProtocolException("The child sent a frame the protocol does not have here.");
            }
        }
    }

    private static async Task AnswerAsync(
        JsonElement request, Stream toChild, RequestBridge bridge, SandboxLimits limits, RunState state, Action<string> log, CancellationToken cancellationToken)
    {
        var id = Messages.Number(request, "id");
        if (id != state.Requests)
        {
            throw new SandboxProtocolException("The child's request ids are out of order.");
        }

        var options = Messages.Text(request, "options", FrameCodec.MaxFromChild);
        byte[] answer;
        state.Clock.Pause();
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(limits.TotalTime);
            answer = await bridge.HandleAsync(id, options, wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RunBreachException($"the call took more than {limits.TotalTime.TotalSeconds:0.#} s in all", state.Requests);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"A request of a sandboxed script failed in the server: {ex.GetType().Name}.");
            answer = RequestBridge.DispatchFailed(id);
        }
        finally
        {
            state.Clock.Resume();
        }

        if (answer.Length > FrameCodec.MaxToChild)
        {
            answer = Messages.Build(w =>
            {
                w.WriteString("t", "response");
                w.WriteNumber("id", id);
                w.WriteStartObject("error");
                w.WriteString("type", "RangeError");
                w.WriteString("message", "The answer is too large for the sandbox; narrow the request.");
                w.WriteEndObject();
            });
        }

        await FrameCodec.WriteAsync(toChild, answer, FrameCodec.MaxToChild, cancellationToken).ConfigureAwait(false);
    }

    private static SandboxOutcome Result(JsonElement result, SandboxLimits limits, int requests)
    {
        if (!Messages.Flag(result, "ok"))
        {
            return SandboxOutcome.Error(Safe(Messages.Text(result, "error", 4000), 600), requests);
        }

        var value = Messages.Text(result, "value", FrameCodec.MaxFromChild);
        var note = Messages.Text(result, "note", 2000, required: false);
        var cutNote = $"The result is longer than {limits.MaxOutputBytes / 1024} KiB and was cut there. Return less: filter, map to the fields you need, or slice.";
        if (Encoding.UTF8.GetByteCount(value) > limits.MaxOutputBytes)
        {
            return new SandboxOutcome(false, CutAtBytes(value, limits.MaxOutputBytes), note.Length == 0 ? cutNote : cutNote + " " + Safe(note, 600), requests);
        }

        if (Messages.Flag(result, "truncated"))
        {
            return new SandboxOutcome(false, value, note.Length == 0 ? cutNote : cutNote + " " + Safe(note, 600), requests);
        }

        return new SandboxOutcome(false, value, note.Length == 0 ? null : Safe(note, 600), requests);
    }

    /// <summary>The script's own error message, bounded and without control characters, as what the child said is not trusted to be short or plain.</summary>
    private static string Safe(string text, int max)
    {
        var clean = new string([.. text.Select(c => char.IsControl(c) ? ' ' : c)]);
        return clean.Length <= max ? clean : clean[..max] + "...";
    }

    private static string CutAtBytes(string text, int maxBytes)
    {
        var length = 0;
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (bytes + size > maxBytes)
            {
                break;
            }

            bytes += size;
            length += rune.Utf16SequenceLength;
        }

        return text[..length];
    }
}
