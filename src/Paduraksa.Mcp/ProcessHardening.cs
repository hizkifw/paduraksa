using System.Runtime.InteropServices;

namespace Paduraksa.Mcp;

/// <summary>
/// What the child applies to itself before it reads a script (docs/design/admin-surfaces.md "MCP in code mode (M10)"), on Linux, each by a system call that can fail: the outcome of every one is a line
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
    /// <summary>The start of the report line when the child closed something: the pool logs it as the server leaking a descriptor.</summary>
    internal const string InheritedClosedPrefix = "Inherited descriptors: closed";

    private const int FGetFd = 1;
    private const int FdCloexec = 1;
    private const int MaxScannedDescriptors = 1 << 16;
    private const long SysLandlockCreateRuleset = 444;
    private const long SysLandlockRestrictSelf = 446;
    private const uint LandlockAccessFsAbi1 = (1u << 13) - 1;
    private const uint LandlockAccessFsRefer = 1u << 13;
    private const uint LandlockAccessFsTruncate = 1u << 14;
    private const uint LandlockAccessFsIoctlDev = 1u << 15;
    private const uint LandlockRestrictSelfTsync = 1u << 3;
    private const uint LandlockCreateRulesetVersion = 1u << 0;
    private const int LandlockNetworkAbi = 4;
    private const ulong LandlockAccessNetBindTcp = 1ul << 0;
    private const ulong LandlockAccessNetConnectTcp = 1ul << 1;
    private const int LandlockScopeAbi = 6;
    private const ulong LandlockScopeSignal = 1ul << 1;

    /// <summary>
    /// Closes every descriptor the child inherited from the server, before anything else runs. A descriptor survives <c>exec</c> only if it lacks close-on-exec, so the ones the child holds without it are exactly
    /// the inherited ones (the runtime opens its own with it, so none of its descriptors is caught here, and a child no descriptor leaked to closes nothing). Landlock and seccomp do not touch a descriptor that is already open,
    /// so a listener, a client connection or a database file that leaked across <c>exec</c> would be usable by a script that got out of the interpreter. .NET opens its own descriptors with close-on-exec, but a native library
    /// (OpenSSL reading the CA bundle with <c>fopen</c>) can hold one without it for a moment, and a child started at that moment inherits it. Stdin, stdout and stderr, the protocol, are kept.
    /// </summary>
    public static string CloseInheritedDescriptors()
    {
        if (!OperatingSystem.IsLinux())
        {
            return "Inherited descriptors: not Linux, none closed";
        }

        List<string> closed = [];
        foreach (var fd in OpenDescriptors())
        {
            var flags = fcntl(fd, FGetFd, 0);
            if (flags < 0 || (flags & FdCloexec) != 0)
            {
                continue;
            }

            // Named before it closes, so the log says what reached the child (QUIC's UDP listeners did: libmsquic opens them
            // without close-on-exec). Only the kind for sockets and pipes, whose targets are inode numbers.
            var kind = KindOf(fd);
            if (close(fd) == 0)
            {
                closed.Add(kind);
            }
        }

        return closed.Count == 0
            ? "Inherited descriptors: none"
            : $"{InheritedClosedPrefix} {closed.Count} ({string.Join(", ", closed.GroupBy(k => k, StringComparer.Ordinal).Select(g => g.Count() == 1 ? g.Key : $"{g.Count()} x {g.Key}"))})";
    }

    /// <summary>What a descriptor is, for the log: "socket", "pipe", an anonymous inode's kind, or a file's path (at most 120 characters).</summary>
    private static string KindOf(int fd)
    {
        string? target;
        try
        {
            target = new FileInfo($"/proc/self/fd/{fd}").LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            target = null;
        }

        return target switch
        {
            null => "unknown",
            _ when target.StartsWith("socket:", StringComparison.Ordinal) => "socket",
            _ when target.StartsWith("pipe:", StringComparison.Ordinal) => "pipe",
            _ when target.StartsWith("anon_inode:", StringComparison.Ordinal) => target,
            _ => target.Length <= 120 ? target : string.Concat(target.AsSpan(0, 117), "..."),
        };
    }

    /// <summary>The descriptors above stderr: from <c>/proc/self/fd</c>, or every number up to the soft limit where there is no <c>/proc</c>.</summary>
    private static List<int> OpenDescriptors()
    {
        List<int> descriptors = [];
        try
        {
            foreach (var name in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
            {
                if (int.TryParse(Path.GetFileName(name), out var fd) && fd > 2)
                {
                    descriptors.Add(fd);
                }
            }

            return descriptors;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var limit = new RLimit();
            var upTo = getrlimit(RlimitNofile, ref limit) == 0 ? (int)Math.Min(limit.Current, MaxScannedDescriptors) : MaxScannedDescriptors;
            return [.. Enumerable.Range(3, Math.Max(0, upTo - 3))];
        }
    }

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

    /// <summary>
    /// Every file system access the kernel at <paramref name="abi"/> can handle. A ruleset must name what it handles and an access it does not name is allowed, so a mask stuck at
    /// ABI 1 would leave <c>truncate(2)</c> (ABI 3) free to empty the databases and the key file. A bit the kernel does not know is EINVAL, which is why the mask follows the ABI.
    /// Later ABIs add no file system access bit.
    /// </summary>
    internal static uint FileSystemAccessMask(int abi) =>
        LandlockAccessFsAbi1
        | (abi >= 2 ? LandlockAccessFsRefer : 0)
        | (abi >= 3 ? LandlockAccessFsTruncate : 0)
        | (abi >= 5 ? LandlockAccessFsIoctlDev : 0);

    /// <summary>Denies every file system access from here on (<paramref name="allThreads"/> false: this thread only, which a test needs so as not to close the file system of the whole test process), for this process and its threads (Landlock, ABI 1: the kernel's own refusal, no rule allows anything).</summary>
    public static string RestrictFileSystem(bool allThreads = true)
    {
        if (!OperatingSystem.IsLinux())
        {
            return "Landlock: not Linux";
        }

        try
        {
            var abi = landlockVersion(SysLandlockCreateRuleset, 0, 0, LandlockCreateRulesetVersion);
            if (abi < 1)
            {
                return "Landlock: not available (errno " + Marshal.GetLastPInvokeError() + ")";
            }

            var attributes = new LandlockRulesetAttr { HandledAccessFs = FileSystemAccessMask((int)abi) };
            var fd = syscall(SysLandlockCreateRuleset, ref attributes, (nuint)Marshal.SizeOf<LandlockRulesetAttr>(), 0);
            if (fd < 0)
            {
                return "Landlock: not available (errno " + Marshal.GetLastPInvokeError() + ")";
            }

            // Newer kernels can apply it to every thread of the process; on older ones it applies to this thread and the threads it creates, which is where scripts run.
            var all = allThreads && syscall(SysLandlockRestrictSelf, (nint)fd, LandlockRestrictSelfTsync, 0) == 0;
            var restricted = all || syscall(SysLandlockRestrictSelf, (nint)fd, 0, 0) == 0;
            _ = close((int)fd);
            return restricted ? $"Landlock: no file system access, ABI {abi}" + (all ? " (every thread)" : " (the script thread)") : "Landlock: not applied (errno " + Marshal.GetLastPInvokeError() + ")";
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return "Landlock: not available";
        }
    }

    /// <summary>
    /// Denies every TCP bind and connect (Landlock ABI 4, Linux 6.7: a ruleset that handles both accesses and allows no port). Landlock does not see UDP or raw sockets, which
    /// <see cref="SyscallFilter"/> closes; this is the kernel's refusal that holds on a kernel too new for the filter's assumptions and covers io_uring's connect too.
    /// </summary>
    public static string RestrictNetwork(bool allThreads = true)
    {
        if (!OperatingSystem.IsLinux())
        {
            return "Landlock network: not Linux";
        }

        try
        {
            var abi = landlockVersion(SysLandlockCreateRuleset, 0, 0, LandlockCreateRulesetVersion);
            if (abi < LandlockNetworkAbi)
            {
                return abi < 0 ? "Landlock network: not available (errno " + Marshal.GetLastPInvokeError() + ")" : "Landlock network: not available (ABI " + abi + ", needs " + LandlockNetworkAbi + ")";
            }

            var attributes = new LandlockNetworkRulesetAttr { HandledAccessNet = LandlockAccessNetBindTcp | LandlockAccessNetConnectTcp };
            var fd = syscall(SysLandlockCreateRuleset, ref attributes, (nuint)Marshal.SizeOf<LandlockNetworkRulesetAttr>(), 0);
            if (fd < 0)
            {
                return "Landlock network: not available (errno " + Marshal.GetLastPInvokeError() + ")";
            }

            var all = allThreads && syscall(SysLandlockRestrictSelf, (nint)fd, LandlockRestrictSelfTsync, 0) == 0;
            var restricted = all || syscall(SysLandlockRestrictSelf, (nint)fd, 0, 0) == 0;
            _ = close((int)fd);
            return restricted ? "Landlock network: no TCP bind or connect" + (all ? " (every thread)" : " (the script thread)") : "Landlock network: not applied (errno " + Marshal.GetLastPInvokeError() + ")";
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return "Landlock network: not available";
        }
    }

    /// <summary>
    /// Refuses every signal to a process outside the child's Landlock domain, the parent first (Landlock ABI 6, Linux 6.12: <c>LANDLOCK_SCOPE_SIGNAL</c>). It covers what
    /// <see cref="SyscallFilter"/> checks by argument and also the signal the kernel sends a descriptor's owner on I/O. Applied only to every thread at once: on a kernel
    /// that can restrict only this thread, the runtime's other threads would be outside the domain, and a GC on the script thread signals them to suspend.
    /// </summary>
    public static string RestrictSignals(bool allThreads = true)
    {
        if (!OperatingSystem.IsLinux())
        {
            return "Landlock signals: not Linux";
        }

        try
        {
            var abi = landlockVersion(SysLandlockCreateRuleset, 0, 0, LandlockCreateRulesetVersion);
            if (abi < LandlockScopeAbi)
            {
                return abi < 0 ? "Landlock signals: not available (errno " + Marshal.GetLastPInvokeError() + ")" : "Landlock signals: not available (ABI " + abi + ", needs " + LandlockScopeAbi + ")";
            }

            var attributes = new LandlockScopedRulesetAttr { Scoped = LandlockScopeSignal };
            var fd = syscall(SysLandlockCreateRuleset, ref attributes, (nuint)Marshal.SizeOf<LandlockScopedRulesetAttr>(), 0);
            if (fd < 0)
            {
                return "Landlock signals: not available (errno " + Marshal.GetLastPInvokeError() + ")";
            }

            var restricted = syscall(SysLandlockRestrictSelf, (nint)fd, allThreads ? LandlockRestrictSelfTsync : 0, 0) == 0;
            var errno = Marshal.GetLastPInvokeError();
            _ = close((int)fd);
            return restricted
                ? "Landlock signals: none to a process outside the sandbox" + (allThreads ? " (every thread)" : " (this thread)")
                : allThreads ? "Landlock signals: not applied (the kernel cannot restrict every thread at once, errno " + errno + ")" : "Landlock signals: not applied (errno " + errno + ")";
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return "Landlock signals: not available";
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

    [StructLayout(LayoutKind.Sequential)]
    private struct LandlockNetworkRulesetAttr
    {
        public ulong HandledAccessFs;
        public ulong HandledAccessNet;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LandlockScopedRulesetAttr
    {
        public ulong HandledAccessFs;
        public ulong HandledAccessNet;
        public ulong Scoped;
    }

    [DllImport("libc", EntryPoint = "setrlimit", SetLastError = true)]
    private static extern int setrlimit(int resource, ref RLimit limit);

    [DllImport("libc", EntryPoint = "getrlimit", SetLastError = true)]
    private static extern int getrlimit(int resource, ref RLimit limit);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int fcntl(int fd, int command, int argument);

    [DllImport("libc", EntryPoint = "prctl", SetLastError = true)]
    private static extern int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long syscall(long number, ref LandlockRulesetAttr attributes, nuint size, uint flags);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long syscall(long number, ref LandlockNetworkRulesetAttr attributes, nuint size, uint flags);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long syscall(long number, ref LandlockScopedRulesetAttr attributes, nuint size, uint flags);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long landlockVersion(long number, nint attributes, nuint size, uint flags);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long syscall(long number, nint fd, uint flags, uint unused);

    [DllImport("libc", EntryPoint = "close")]
    private static extern int close(int fd);
}
