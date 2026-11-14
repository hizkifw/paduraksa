using System.Text.Json;

namespace Paduraksa.Bench.Soak;

/// <summary>The run's clock and its event log: every phase, change and notable observation, one JSON line each, for the analysis to line up windows with samples.</summary>
internal sealed class Events : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();

    public Events(string path)
    {
        _writer = new StreamWriter(path, append: false) { AutoFlush = true };
        StartUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>Unix milliseconds when the load started (the first sample's zero).</summary>
    public long StartUnixMs { get; private set; }

    public double ElapsedSeconds => (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - StartUnixMs) / 1000.0;

    /// <summary>Restarts the clock: the load's zero is when setup and the first publish are done.</summary>
    public void ResetClock() => StartUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public void Write(string kind, object? detail = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var seconds = (now - StartUnixMs) / 1000.0;
        var line = JsonSerializer.Serialize(new { t = seconds, ms = now, kind, detail });
        lock (_gate)
        {
            _writer.WriteLine(line);
        }

        Console.WriteLine($"[{seconds,8:F1}s] {kind}{(detail is null ? string.Empty : " " + JsonSerializer.Serialize(detail))}");
    }

    public void Dispose() => _writer.Dispose();
}
