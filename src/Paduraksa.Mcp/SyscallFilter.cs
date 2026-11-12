using System.Runtime.InteropServices;

namespace Paduraksa.Mcp;

/// <summary>
/// A seccomp filter for the child (PLAN §11.2): the child is the boundary, and this closes the routes out of it. It lets the child create only <c>AF_UNIX</c> sockets (it talks
/// over pipes and needs none), so it has no TCP, UDP, raw or netlink socket to send what it saw through, and it refuses the calls in <see cref="Denied"/>, which are the
/// ways from a process to another process of the same user (the parent, with the vault, the tokens and the network) or around this filter. Every other system call is allowed:
/// this is not a profile of the runtime's calls, which would break with every .NET release. A refused call fails with <c>EPERM</c>, not a kill, so a library that probes
/// does not take the child down.
/// </summary>
internal static class SyscallFilter
{
    private const int PrSetSeccomp = 22;
    private const uint SeccompModeFilter = 2;
    private const uint SeccompSetModeFilter = 1;
    private const uint SeccompFilterFlagTsync = 1;
    private const uint AuditArchX8664 = 0xC000003E;
    private const uint AuditArchAarch64 = 0xC00000B7;
    private const uint X32SyscallBit = 0x40000000;
    private const uint AfUnix = 1;
    private const uint SeccompRetAllow = 0x7FFF0000;
    private const uint SeccompRetErrnoEperm = 0x00050000 | 1;
    private const ushort BpfLdAbsWord = 0x20;
    private const ushort BpfJeq = 0x15;
    private const ushort BpfJset = 0x45;
    private const ushort BpfRet = 0x06;
    private const uint OffsetNr = 0;
    private const uint OffsetArch = 4;
    private const uint OffsetArg0 = 16;

    /// <summary>
    /// The calls refused outright, one reason each. The numbers are the kernel's (<c>asm/unistd_64.h</c> for x86-64, <c>asm-generic/unistd.h</c> for arm64), which differ by
    /// architecture; <c>SyscallFilterTests</c> pins every one against a reference copy, so a typo fails a test. To close another route, add a row here and nowhere else.
    /// </summary>
    internal static IReadOnlyList<DeniedCall> Denied { get; } =
    [
        new("ptrace", 101, 117, "attach to, read and write another process of the user, the parent first"),
        new("process_vm_readv", 310, 270, "read the parent's memory without ptrace"),
        new("process_vm_writev", 311, 271, "write the parent's memory without ptrace"),
        new("pidfd_open", 434, 434, "a handle on the parent, the first step to pidfd_getfd"),
        new("pidfd_getfd", 438, 438, "duplicate the parent's descriptors: its database files, sockets and listeners"),
        new("kcmp", 312, 272, "learn which of another process's descriptors are which kernel objects, to pick one for pidfd_getfd"),
        new("bpf", 321, 280, "load kernel programs, a way around every filter and a source of kernel bugs"),
        new("userfaultfd", 323, 282, "pause the kernel inside a page fault, the tool that makes kernel races winnable"),
        new("perf_event_open", 298, 241, "performance counters of another process: a side channel on the parent, and a source of kernel bugs"),
        new("io_uring_setup", 425, 425, "run socket, connect and open as queued operations that this filter would not see"),
        new("io_uring_enter", 426, 426, "submit those operations on a ring made earlier"),
        new("io_uring_register", 427, 427, "register buffers and files for them"),
    ];

    /// <summary>The calls whose first argument is a socket domain, of which only <c>AF_UNIX</c> is allowed (<c>socketpair</c> makes sockets as <c>socket</c> does).</summary>
    internal static IReadOnlyList<DeniedCall> DomainChecked { get; } =
    [
        new("socket", 41, 198, "a TCP, UDP, raw or netlink socket to send data out through"),
        new("socketpair", 53, 199, "the same, in pairs: the kernel refuses a non-local domain today, and this does not rely on it"),
    ];

    /// <summary>The architectures the filter has been written for: a call number is per architecture, and a guess would silently deny or allow the wrong call.</summary>
    internal static FilterTarget? TargetOf(Architecture architecture) => architecture switch
    {
        Architecture.X64 => new(AuditArchX8664, 317, HasX32: true, Architecture.X64),
        Architecture.Arm64 => new(AuditArchAarch64, 277, HasX32: false, Architecture.Arm64),
        _ => null,
    };

    /// <summary>Applies the filter to every thread (<paramref name="allThreads"/>, where the kernel allows it) or to this thread and the ones it creates, which is where scripts run. It needs <c>no_new_privs</c>, which <see cref="ProcessHardening.Apply"/> has set.</summary>
    public static string Restrict(bool allThreads = true)
    {
        if (!OperatingSystem.IsLinux())
        {
            return "seccomp: not Linux";
        }

        if (TargetOf(RuntimeInformation.ProcessArchitecture) is not { } target)
        {
            return "seccomp: not available (architecture " + RuntimeInformation.ProcessArchitecture + ")";
        }

        var program = Build(target);
        var memory = Marshal.AllocHGlobal(program.Length * Marshal.SizeOf<SockFilter>());
        try
        {
            for (var i = 0; i < program.Length; i++)
            {
                Marshal.StructureToPtr(program[i], memory + (i * Marshal.SizeOf<SockFilter>()), false);
            }

            var fprog = new SockFprog { Length = (ushort)program.Length, Filter = memory };
            var all = allThreads && seccomp(target.SeccompNumber, SeccompSetModeFilter, SeccompFilterFlagTsync, ref fprog) == 0;
            var applied = all || prctl(PrSetSeccomp, SeccompModeFilter, ref fprog, 0, 0) == 0;
            return applied
                ? $"seccomp: only AF_UNIX sockets and {Denied.Count} calls denied" + (all ? " (every thread)" : " (the script thread)")
                : "seccomp: not applied (errno " + Marshal.GetLastPInvokeError() + ")";
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return "seccomp: not available";
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    /// <summary>The program: a foreign architecture, an x32 call number and every call in <see cref="Denied"/> end in the denied return, a <see cref="DomainChecked"/> call unless its domain is <c>AF_UNIX</c>, everything else in the allowed one.</summary>
    internal static SockFilter[] Build(FilterTarget target)
    {
        // A jump target is an instruction index: these stand for the two returns appended at the end and for the domain check.
        const int Allow = -1;
        const int Deny = -2;
        const int Domain = -3;
        List<(ushort Code, int IfTrue, int IfFalse, uint K)> steps =
        [
            (BpfLdAbsWord, 0, 0, OffsetArch),
            (BpfJeq, 0, Deny, target.AuditArch),
            (BpfLdAbsWord, 0, 0, OffsetNr),
        ];
        if (target.HasX32)
        {
            // The x32 ABI shares the architecture value and sets this bit on every call number, which would otherwise miss the compares below.
            steps.Add((BpfJset, Deny, 0, X32SyscallBit));
        }

        foreach (var call in Denied)
        {
            steps.Add((BpfJeq, Deny, 0, call.Number.For(target.Architecture)));
        }

        // A call that is not one of these is allowed (the second compare's false branch); one that is goes to the domain check, which is the next step after the second compare.
        steps.Add((BpfJeq, Domain, 0, DomainChecked[0].Number.For(target.Architecture)));
        steps.Add((BpfJeq, 0, Allow, DomainChecked[1].Number.For(target.Architecture)));
        var domainIndex = steps.Count;
        steps.Add((BpfLdAbsWord, 0, 0, OffsetArg0));
        steps.Add((BpfJeq, Allow, Deny, AfUnix));

        var allowIndex = steps.Count;
        var denyIndex = steps.Count + 1;
        var program = new SockFilter[steps.Count + 2];
        for (var i = 0; i < steps.Count; i++)
        {
            var (code, ifTrue, ifFalse, k) = steps[i];
            program[i] = new SockFilter { Code = code, Jt = Offset(ifTrue, i), Jf = Offset(ifFalse, i), K = k };
        }

        program[allowIndex] = new SockFilter { Code = BpfRet, K = SeccompRetAllow };
        program[denyIndex] = new SockFilter { Code = BpfRet, K = SeccompRetErrnoEperm };
        return program;

        byte Offset(int target, int from) => target switch
        {
            Allow => (byte)(allowIndex - from - 1),
            Deny => (byte)(denyIndex - from - 1),
            Domain => (byte)(domainIndex - from - 1),
            _ => (byte)target,
        };
    }

    internal readonly record struct SyscallNumbers(uint X64, uint Arm64)
    {
        public uint For(Architecture architecture) => architecture == Architecture.Arm64 ? Arm64 : X64;
    }

    internal sealed record DeniedCall(string Name, SyscallNumbers Number, string Reason)
    {
        public DeniedCall(string name, uint x64, uint arm64, string reason)
            : this(name, new SyscallNumbers(x64, arm64), reason)
        {
        }
    }

    /// <param name="AuditArch">The value the kernel reports for the architecture's calling convention.</param>
    /// <param name="SeccompNumber">The number of <c>seccomp(2)</c>, which the thread-synchronized install needs.</param>
    /// <param name="HasX32">Whether the architecture has a second ABI that sets <see cref="X32SyscallBit"/>.</param>
    /// <param name="Architecture">Which column of the call tables to read.</param>
    internal sealed record FilterTarget(uint AuditArch, long SeccompNumber, bool HasX32, Architecture Architecture);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SockFilter
    {
        public ushort Code;
        public byte Jt;
        public byte Jf;
        public uint K;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SockFprog
    {
        public ushort Length;
        public nint Filter;
    }

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long seccomp(long number, uint operation, uint flags, ref SockFprog program);

    [DllImport("libc", EntryPoint = "prctl", SetLastError = true)]
    private static extern int prctl(int option, uint mode, ref SockFprog program, ulong arg4, ulong arg5);
}
