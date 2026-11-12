using System.Runtime.InteropServices;

namespace Paduraksa.Mcp;

/// <summary>A child's resident memory and CPU time, read from <c>/proc</c> (the parent's view of the process, not the child's own account of itself).</summary>
internal static class ProcStats
{
    private const int ScClkTck = 2;

    /// <summary>The unit of the times in <c>/proc/&lt;pid&gt;/stat</c>: <c>sysconf(_SC_CLK_TCK)</c>, read once (100 on every mainstream Linux, but asked rather than assumed).</summary>
    private static readonly double TicksPerSecond = ReadTicksPerSecond();

    /// <returns>Resident bytes, or <c>-1</c> when the process is gone or the platform has no <c>/proc</c>.</returns>
    public static long ResidentBytes(int pid)
    {
        try
        {
            foreach (var line in File.ReadLines($"/proc/{pid}/status"))
            {
                if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
                {
                    var digits = new string([.. line.Where(char.IsAsciiDigit)]);
                    return long.TryParse(digits, out var kilobytes) ? kilobytes * 1024 : -1;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // gone, or not Linux
        }

        return -1;
    }

    /// <returns>User plus system CPU time, or <c>null</c> when it cannot be read.</returns>
    public static TimeSpan? CpuTime(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            // The fields after the command name, which is in parentheses and may hold spaces: state is the first, utime and stime the 12th and 13th.
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            var ticks = long.Parse(fields[11], System.Globalization.CultureInfo.InvariantCulture) + long.Parse(fields[12], System.Globalization.CultureInfo.InvariantCulture);
            return TimeSpan.FromSeconds(ticks / TicksPerSecond);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static double ReadTicksPerSecond()
    {
        try
        {
            var ticks = OperatingSystem.IsLinux() ? sysconf(ScClkTck) : 0;
            return ticks > 0 ? ticks : 100;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return 100;
        }
    }

    [DllImport("libc", EntryPoint = "sysconf")]
    private static extern long sysconf(int name);
}
