using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Paduraksa.Dashboard.AboutArea;

/// <summary>What the About page says about the process: the runtime, the operating system and when it started. Nothing that names a path or the environment.</summary>
/// <param name="Runtime">The .NET runtime, for example <c>.NET 10.0.7</c>.</param>
/// <param name="OperatingSystem">The operating system as the runtime describes it.</param>
/// <param name="Architecture">The process's architecture (<c>x64</c>, <c>arm64</c>).</param>
/// <param name="StartedAt">When the process started.</param>
public sealed record ServerFacts(string Runtime, string OperatingSystem, string Architecture, DateTimeOffset StartedAt)
{
    public static ServerFacts Capture()
    {
        using var process = Process.GetCurrentProcess();
        return new ServerFacts(
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            new DateTimeOffset(process.StartTime.ToUniversalTime()));
    }

    /// <summary>How long the process has run, in its two largest units: "3 d 4 h", "2 h 15 min", "12 min", "40 s".</summary>
    public static string Uptime(TimeSpan span) => span switch
    {
        { TotalDays: >= 1 } => $"{(int)span.TotalDays} d {span.Hours} h",
        { TotalHours: >= 1 } => $"{(int)span.TotalHours} h {span.Minutes} min",
        { TotalMinutes: >= 1 } => $"{(int)span.TotalMinutes} min",
        _ => $"{Math.Max(0, (int)span.TotalSeconds)} s",
    };
}
