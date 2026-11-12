using System.Runtime.InteropServices;

namespace Paduraksa.Mcp;

/// <summary>
/// What the child applies to itself before it reads a script (PLAN §11.2), on Linux, each by a system call that can fail: the outcome of every one is a line
/// in the startup frame, and the parent logs the ones that did not apply. Several are per thread, so they are applied on the thread that runs scripts. They narrow what a script that got out of the interpreter could do; the parent's
/// limits and its kill are what hold regardless.
/// </summary>
internal static class ProcessHardening
{
    private const int RlimitCore = 4;
    private const int RlimitCpu = 0;
    private const int RlimitNofile = 7;
    private const int PrSetPdeathsig = 1;
    private const int PrSetNoNewPrivs = 38;
    private const int SigKill = 9;
    private const long SysLandlockCreateRuleset = 444;
    private const long SysLandlockRestrictSelf = 446;
    private const uint LandlockAccessFsAll = (1u << 13) - 1;
    private const uint LandlockRestrictSelfTsync = 1u << 3;

    /// <summary>The limits and flags that cost nothing to apply early. Landlock waits for <see cref="RestrictFileSystem"/>, because the runtime still has assemblies to load.</summary>
    public static List<string> Apply(int cpuSeconds)
    {
        List<string> report = [];
        if (!OperatingSystem.IsLinux())
        {
            report.Add("not Linux: no process hardening");
            return report;
        }

        Record(report, "RLIMIT_CPU", SetLimit(RlimitCpu, (ulong)cpuSeconds));
        Record(report, "RLIMIT_NOFILE", SetLimit(RlimitNofile, 256));
        Record(report, "RLIMIT_CORE", SetLimit(RlimitCore, 0));
        Record(report, "PR_SET_NO_NEW_PRIVS", prctl(PrSetNoNewPrivs, 1, 0, 0, 0) == 0);
        Record(report, "PR_SET_PDEATHSIG", prctl(PrSetPdeathsig, SigKill, 0, 0, 0) == 0);
        return report;
    }

    /// <summary>Sets <c>PR_SET_NO_NEW_PRIVS</c> on this thread, which Landlock requires of whoever restricts itself.</summary>
    public static bool SetNoNewPrivs() => OperatingSystem.IsLinux() && prctl(PrSetNoNewPrivs, 1, 0, 0, 0) == 0;

    /// <summary>Denies every file system access from here on (<paramref name="allThreads"/> false: this thread only, which a test needs so as not to close the file system of the whole test process), for this process and its threads (Landlock, ABI 1: the kernel's own refusal, no rule allows anything).</summary>
    public static string RestrictFileSystem(bool allThreads = true)
    {
        if (!OperatingSystem.IsLinux())
        {
            return "Landlock: not Linux";
        }

        try
        {
            var attributes = new LandlockRulesetAttr { HandledAccessFs = LandlockAccessFsAll };
            var fd = syscall(SysLandlockCreateRuleset, ref attributes, (nuint)Marshal.SizeOf<LandlockRulesetAttr>(), 0);
            if (fd < 0)
            {
                return "Landlock: not available (errno " + Marshal.GetLastPInvokeError() + ")";
            }

            // Newer kernels can apply it to every thread of the process; on older ones it applies to this thread and the threads it creates, which is where scripts run.
            var all = allThreads && syscall(SysLandlockRestrictSelf, (nint)fd, LandlockRestrictSelfTsync, 0) == 0;
            var restricted = all || syscall(SysLandlockRestrictSelf, (nint)fd, 0, 0) == 0;
            _ = close((int)fd);
            return restricted ? "Landlock: no file system access" + (all ? " (every thread)" : " (the script thread)") : "Landlock: not applied (errno " + Marshal.GetLastPInvokeError() + ")";
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return "Landlock: not available";
        }
    }

    private static bool SetLimit(int resource, ulong value)
    {
        var limit = new RLimit { Current = value, Maximum = value };
        return setrlimit(resource, ref limit) == 0;
    }

    private static void Record(List<string> report, string name, bool applied) => report.Add(name + ": " + (applied ? "applied" : "not applied"));

    [StructLayout(LayoutKind.Sequential)]
    private struct RLimit
    {
        public ulong Current;
        public ulong Maximum;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LandlockRulesetAttr
    {
        public ulong HandledAccessFs;
    }

    [DllImport("libc", EntryPoint = "setrlimit", SetLastError = true)]
    private static extern int setrlimit(int resource, ref RLimit limit);

    [DllImport("libc", EntryPoint = "prctl", SetLastError = true)]
    private static extern int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long syscall(long number, ref LandlockRulesetAttr attributes, nuint size, uint flags);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long syscall(long number, nint fd, uint flags, uint unused);

    [DllImport("libc", EntryPoint = "close")]
    private static extern int close(int fd);
}
