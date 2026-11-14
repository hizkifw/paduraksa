using System.Diagnostics;
using System.Runtime.InteropServices;

using Paduraksa.Mcp;

using static Paduraksa.Mcp.SyscallFilter;

namespace Paduraksa.Tests.Integration.Mcp;

/// <summary>
/// The child's seccomp filter (docs/design/admin-surfaces.md "MCP in code mode (M10)", threat model G2). The program is checked in two ways: by a small interpreter of classic BPF over every architecture's
/// numbers, against a reference table kept here on purpose (a typo in the filter's own table fails a test instead of agreeing with itself), and, on this kernel, by
/// installing it on a thread of its own and making the calls.
/// </summary>
public sealed class SyscallFilterTests
{
    private const uint AuditArchX8664 = 0xC000003E;
    private const uint AuditArchAarch64 = 0xC00000B7;
    private const uint AuditArchI386 = 0x40000003;
    private const long Eperm = 1;

    /// <summary>
    /// The kernel's numbers: x86-64 from <c>arch/x86/entry/syscalls/syscall_64.tbl</c> (<c>/usr/include/asm/unistd_64.h</c>), arm64 from <c>include/uapi/asm-generic/unistd.h</c>.
    /// </summary>
    private static readonly (string Name, uint X64, uint Arm64)[] Reference =
    [
        ("ptrace", 101, 117),
        ("process_vm_readv", 310, 270),
        ("process_vm_writev", 311, 271),
        ("pidfd_open", 434, 434),
        ("pidfd_getfd", 438, 438),
        ("kcmp", 312, 272),
        ("bpf", 321, 280),
        ("userfaultfd", 323, 282),
        ("perf_event_open", 298, 241),
        ("io_uring_setup", 425, 425),
        ("io_uring_enter", 426, 426),
        ("io_uring_register", 427, 427),
        ("pidfd_send_signal", 424, 424),
    ];

    /// <summary>The calls allowed only to signal the process itself, by the same tables.</summary>
    private static readonly (string Name, uint X64, uint Arm64)[] ReferenceSignals =
    [
        ("kill", 62, 129),
        ("tkill", 200, 130),
        ("tgkill", 234, 131),
        ("rt_sigqueueinfo", 129, 138),
        ("rt_tgsigqueueinfo", 297, 240),
    ];

    private static readonly (string Name, uint X64, uint Arm64) ReferenceFcntl = ("fcntl", 72, 25);
    private static readonly (string Name, uint X64, uint Arm64) ReferenceIoctl = ("ioctl", 16, 29);

    /// <summary>The pid the programs below are built for; any value does, the kernel tests use the real one.</summary>
    private const int Self = 4242;

    private static readonly (string Name, uint X64, uint Arm64) ReferenceSocket = ("socket", 41, 198);
    private static readonly (string Name, uint X64, uint Arm64) ReferenceSocketpair = ("socketpair", 53, 199);

    /// <summary>Calls the runtime needs, by architecture: read, write, mmap, futex, getpid, tgkill, clone3, exit_group, openat.</summary>
    private static readonly uint[] HarmlessX64 = [0, 1, 9, 202, 39, 234, 435, 231, 257];

    private static readonly uint[] HarmlessArm64 = [63, 64, 222, 98, 172, 131, 435, 94, 56];

    public static TheoryData<Architecture> Architectures => [Architecture.X64, Architecture.Arm64];

    [Fact]
    public void EveryDeniedCallIsInTheReferenceTable_WithTheKernelsNumberForEachArchitecture()
    {
        Assert.Equal(Reference.Select(r => r.Name).Order(StringComparer.Ordinal), Denied.Select(d => d.Name).Order(StringComparer.Ordinal));
        foreach (var (name, x64, arm64) in Reference)
        {
            var call = Denied.Single(d => d.Name == name);
            Assert.Equal(x64, call.Number.X64);
            Assert.Equal(arm64, call.Number.Arm64);
        }

        foreach (var (name, x64, arm64) in new[] { ReferenceSocket, ReferenceSocketpair })
        {
            var call = DomainChecked.Single(d => d.Name == name);
            Assert.Equal(x64, call.Number.X64);
            Assert.Equal(arm64, call.Number.Arm64);
        }

        Assert.Equal(ReferenceSignals.Select(r => r.Name), SignalsToSelfOnly.Select(d => d.Name));
        foreach (var (name, x64, arm64) in ReferenceSignals.Append(ReferenceFcntl).Append(ReferenceIoctl))
        {
            var call = SignalsToSelfOnly.Concat(OwnerCommands.Select(o => o.Call)).Single(d => d.Name == name);
            Assert.Equal(x64, call.Number.X64);
            Assert.Equal(arm64, call.Number.Arm64);
        }

        Assert.All(Denied.Concat(DomainChecked).Concat(SignalsToSelfOnly), d => Assert.False(string.IsNullOrWhiteSpace(d.Reason), d.Name + " has no reason"));
    }

    [Theory]
    [MemberData(nameof(Architectures))]
    public void TheProgramRefusesEveryCallInTheTable_AndAllowsTheOnesTheRuntimeNeeds(Architecture architecture)
    {
        var (target, arch) = Program(architecture);
        var x64 = architecture == Architecture.X64;
        foreach (var (name, nx64, narm) in Reference)
        {
            Assert.False(Allows(target, arch, x64 ? nx64 : narm, Self), $"{name} on {architecture} was allowed");
        }

        foreach (var number in x64 ? HarmlessX64 : HarmlessArm64)
        {
            Assert.True(Allows(target, arch, number, Self), $"call {number} on {architecture} was refused");
        }

        // The other architecture's numbers mean other calls: x86-64's 117 is setresuid, arm64's 101 is nanosleep. Neither is refused for being the other's ptrace.
        Assert.True(Allows(target, arch, x64 ? 117u : 101u, 0));
    }

    [Theory]
    [MemberData(nameof(Architectures))]
    public void ASignalIsAllowedToTheProcessItselfAndToNoOther_NotItsGroupNorEveryProcess(Architecture architecture)
    {
        var (target, arch) = Program(architecture);
        var x64 = architecture == Architecture.X64;
        foreach (var (name, nx64, narm) in ReferenceSignals)
        {
            var number = x64 ? nx64 : narm;
            Assert.True(Allows(target, arch, number, Self), $"{name} to itself was refused");
            Assert.True(Allows(target, arch, number, Self | (1ul << 32)), $"{name} to itself with high bits the kernel ignores was refused");
            foreach (var other in new ulong[] { 1, Self - 1, Self + 1, 0, 0xFFFFFFFF, unchecked((ulong)-Self), unchecked((ulong)-1L) })
            {
                Assert.False(Allows(target, arch, number, other), $"{name} to {(long)other} was allowed");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Architectures))]
    public void MakingTheParentADescriptorsOwnerIsRefused_AndTheOtherCommandsAreNot(Architecture architecture)
    {
        var (target, arch) = Program(architecture);
        var x64 = architecture == Architecture.X64;
        var fcntl = x64 ? ReferenceFcntl.X64 : ReferenceFcntl.Arm64;
        var ioctl = x64 ? ReferenceIoctl.X64 : ReferenceIoctl.Arm64;
        Assert.False(Allows(target, arch, fcntl, 3, 8), "F_SETOWN was allowed");
        Assert.False(Allows(target, arch, fcntl, 3, 15), "F_SETOWN_EX was allowed");
        Assert.False(Allows(target, arch, ioctl, 3, 0x8901), "FIOSETOWN was allowed");
        Assert.False(Allows(target, arch, ioctl, 3, 0x8902), "SIOCSPGRP was allowed");
        foreach (var command in new ulong[] { 0, 1, 2, 3, 4, 9, 16, 1030 })
        {
            Assert.True(Allows(target, arch, fcntl, 3, command), $"fcntl command {command} was refused");
        }

        foreach (var command in new ulong[] { 0x5401, 0x541B, 0x5421, 0x8903 })
        {
            Assert.True(Allows(target, arch, ioctl, 3, command), $"ioctl command {command:x} was refused");
        }
    }

    [Theory]
    [MemberData(nameof(Architectures))]
    public void ASocketOrASocketpairIsAllowedForAUnixDomainAndForNoOther(Architecture architecture)
    {
        var (target, arch) = Program(architecture);
        var x64 = architecture == Architecture.X64;
        foreach (var (name, nx64, narm) in new[] { ReferenceSocket, ReferenceSocketpair })
        {
            var number = x64 ? nx64 : narm;
            Assert.True(Allows(target, arch, number, 1), $"{name}(AF_UNIX) was refused");
            foreach (var domain in new ulong[] { 2, 10, 16, 17, 40, 0 })
            {
                Assert.False(Allows(target, arch, number, domain), $"{name}(domain {domain}) was allowed");
            }
        }
    }

    [Fact]
    public void AForeignArchitectureAndTheX32AbiAreRefused_WhateverTheCall()
    {
        var (x64, arch) = Program(Architecture.X64);
        Assert.False(Allows(x64, AuditArchI386, 0, 0));
        Assert.False(Allows(x64, AuditArchAarch64, 0, 0));
        Assert.False(Allows(x64, arch, 0 | 0x40000000u, 0), "an x32 read was allowed");
        Assert.False(Allows(x64, arch, 41 | 0x40000000u, 1), "an x32 socket was allowed");
        var (arm, armArch) = Program(Architecture.Arm64);
        Assert.False(Allows(arm, AuditArchX8664, 63, 0));
        Assert.True(Allows(arm, armArch, 63, 0));
    }

    [Fact]
    public void OnThisKernelTheFilterRefusesSocketsOtherThanLocalOnesAndIoUring_ToTheThreadThatAppliedIt()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && TargetOf(RuntimeInformation.ProcessArchitecture) is not null, "no filter for this platform");
        var architecture = RuntimeInformation.ProcessArchitecture;
        var socket = DomainChecked[0].Number.For(architecture);
        var socketpair = DomainChecked[1].Number.For(architecture);
        var ioUringSetup = Denied.Single(d => d.Name == "io_uring_setup").Number.For(architecture);

        // Unfiltered first, on a thread of its own: these are calls that work on any kernel, so a refusal below is the filter's.
        var baseline = RunOnThread(filtered: false, () => (ProbeSocket(socket, 2, 1, 0), ProbePair(socketpair)));
        Assert.True(baseline.Value.Item1 >= 0 && baseline.Value.Item2 >= 0, "this kernel refuses sockets anyway");

        var result = RunOnThread(filtered: true, () => new[]
        {
            ProbeSocket(socket, 2, 1, 0),
            ProbeSocket(socket, 10, 1, 0),
            ProbeSocket(socket, 2, 2, 0),
            Probe(socketpair, 2, 1, 0),
            ProbeIoUring(ioUringSetup),
        });
        Assert.SkipWhen(result.Status is null || !result.Status.Contains("only AF_UNIX", StringComparison.Ordinal), "no seccomp filter here: " + result.Status);
        Assert.All(result.Value, errno => Assert.Equal(-Eperm, errno));

        var local = RunOnThread(filtered: true, () => (ProbeSocket(socket, 1, 1, 0), ProbePair(socketpair)));
        Assert.True(local.Value.Item1 >= 0, "AF_UNIX socket was refused");
        Assert.True(local.Value.Item2 >= 0, "AF_UNIX socketpair was refused");
    }

    [Fact]
    public async Task OnThisKernelTheFilterRefusesPtraceAndPidfdGetfdOnAnotherProcessOfTheUser_ToTheThreadThatAppliedIt()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && TargetOf(RuntimeInformation.ProcessArchitecture) is not null, "no filter for this platform");
        var architecture = RuntimeInformation.ProcessArchitecture;
        var ptrace = Denied.Single(d => d.Name == "ptrace").Number.For(architecture);
        var pidfdOpen = Denied.Single(d => d.Name == "pidfd_open").Number.For(architecture);
        var pidfdGetfd = Denied.Single(d => d.Name == "pidfd_getfd").Number.For(architecture);

        // A process of this user the test's thread has the right to trace, standing in for the parent.
        using var stand = Process.Start(new ProcessStartInfo("sleep", "60") { UseShellExecute = false }) ?? throw new InvalidOperationException("no sleep");
        try
        {
            var pidfd = Probe(pidfdOpen, stand.Id, 0, 0);
            Assert.SkipWhen(pidfd < 0, "this kernel has no pidfd_open");

            // Unfiltered: the calls work here (a tracer's thread ending detaches it), which is what makes the refusal below the filter's.
            var baseline = RunOnThread(filtered: false, () => (Probe(ptrace, 0x4206 /* PTRACE_SEIZE */, stand.Id, 0), Probe(pidfdGetfd, pidfd, 0, 0)));
            Assert.SkipWhen(baseline.Value.Item1 != 0 || baseline.Value.Item2 < 0, $"this kernel does not let a process trace or take descriptors from its own child (ptrace_scope): {baseline.Value}");

            var result = RunOnThread(filtered: true, () => (Probe(ptrace, 0x4206, stand.Id, 0), Probe(pidfdGetfd, pidfd, 0, 0), Probe(ptrace, 16 /* PTRACE_ATTACH */, stand.Id, 0)));
            Assert.SkipWhen(result.Status is null || !result.Status.Contains("only AF_UNIX", StringComparison.Ordinal), "no seccomp filter here: " + result.Status);
            Assert.Equal((-Eperm, -Eperm, -Eperm), result.Value);
        }
        finally
        {
            stand.Kill();
            await stand.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task OnThisKernelTheFilterRefusesEveryWayOfSignallingAnotherProcess_AndAllowsSignallingItself()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && TargetOf(RuntimeInformation.ProcessArchitecture) is not null, "no filter for this platform");
        var architecture = RuntimeInformation.ProcessArchitecture;
        long Number(string name) => SignalsToSelfOnly.Concat(Denied).Concat(OwnerCommands.Select(o => o.Call)).Single(d => d.Name == name).Number.For(architecture);
        var self = Environment.ProcessId;

        // A process of this user standing in for the parent: the probes send signal 0, which checks the permission and delivers nothing, and one real SIGTERM.
        using var stand = Process.Start(new ProcessStartInfo("sleep", "60") { UseShellExecute = false }) ?? throw new InvalidOperationException("no sleep");
        try
        {
            var baseline = RunOnThread(filtered: false, () => Probe(Number("kill"), stand.Id, 0, 0));
            Assert.Equal(0, baseline.Value);

            var result = RunOnThread(filtered: true, () =>
            {
                var tid = Gettid();
                var pipe = new int[2];
                Assert.Equal(0, Pipe(pipe));
                try
                {
                    return new[]
                    {
                        Probe(Number("kill"), stand.Id, 0, 0),
                        Probe(Number("kill"), stand.Id, 15, 0),
                        Probe(Number("kill"), 0, 0, 0),
                        Probe(Number("kill"), -1, 0, 0),
                        Probe(Number("kill"), -self, 0, 0),
                        Probe(Number("tkill"), stand.Id, 0, 0),
                        Probe(Number("tgkill"), stand.Id, stand.Id, 0),
                        Probe(Number("rt_sigqueueinfo"), stand.Id, 0, 0),
                        Probe(Number("rt_tgsigqueueinfo"), stand.Id, stand.Id, 0),
                        Probe(Number("pidfd_send_signal"), 0, 0, 0),
                        Probe(Number("fcntl"), pipe[0], 8 /* F_SETOWN */, stand.Id),
                        Probe(Number("ioctl"), pipe[0], 0x8901 /* FIOSETOWN */, 0),

                        // What the runtime does: signal 0 to itself and to its own thread.
                        Probe(Number("kill"), self, 0, 0),
                        Probe(Number("tgkill"), self, tid, 0),
                        Probe(Number("fcntl"), pipe[0], 1 /* F_GETFD */, 0),
                    };
                }
                finally
                {
                    _ = Close(pipe[0]);
                    _ = Close(pipe[1]);
                }
            });
            Assert.SkipWhen(result.Status is null || !result.Status.Contains("only AF_UNIX", StringComparison.Ordinal), "no seccomp filter here: " + result.Status);
            Assert.Equal([.. Enumerable.Repeat(-Eperm, 12), 0, 0, 0], result.Value);
            Assert.False(stand.HasExited, "the stand-in for the parent was signalled");
        }
        finally
        {
            stand.Kill();
            await stand.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task OnThisKernelLandlockScopesSignalsToTheSandbox()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Landlock is Linux");
        using var stand = Process.Start(new ProcessStartInfo("sleep", "60") { UseShellExecute = false }) ?? throw new InvalidOperationException("no sleep");
        try
        {
            string? status = null;
            long errno = 0;
            Thread thread = new(() =>
            {
                Assert.True(ProcessHardening.SetNoNewPrivs());
                status = ProcessHardening.RestrictSignals(allThreads: false);
                errno = Probe(TargetOf(RuntimeInformation.ProcessArchitecture) is { } target ? SignalsToSelfOnly[0].Number.For(target.Architecture) : 62, stand.Id, 15, 0);
            });
            thread.Start();
            thread.Join();
            Assert.SkipWhen(status is null || !status.Contains("none to a process outside", StringComparison.Ordinal), "this kernel has no Landlock signal scoping (needs Linux 6.12): " + status);
            Assert.Equal(-Eperm, errno);
            Assert.False(stand.HasExited, "the stand-in for the parent was signalled");
        }
        finally
        {
            stand.Kill();
            await stand.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
    }

    private static (SockFilter[] Program, uint Arch) Program(Architecture architecture)
    {
        var target = TargetOf(architecture)!;
        return (Build(target, Self), target.AuditArch);
    }

    /// <summary>Runs <paramref name="program"/> on one system call the way the kernel does, and says whether it is allowed.</summary>
    private static bool Allows(SockFilter[] program, uint arch, uint number, ulong arg0, ulong arg1 = 0)
    {
        uint accumulator = 0;
        var pc = 0;
        while (true)
        {
            var instruction = program[pc++];
            switch (instruction.Code)
            {
                case 0x20: // ld abs, word
                    accumulator = instruction.K switch
                    {
                        0 => number,
                        4 => arch,
                        16 => (uint)arg0,
                        24 => (uint)arg1,
                        _ => throw new InvalidOperationException("the program reads " + instruction.K),
                    };
                    break;
                case 0x15: // jeq k
                    pc += accumulator == instruction.K ? instruction.Jt : instruction.Jf;
                    break;
                case 0x45: // jset k
                    pc += (accumulator & instruction.K) != 0 ? instruction.Jt : instruction.Jf;
                    break;
                case 0x06: // ret k
                    return instruction.K == 0x7FFF0000 ? true : instruction.K == (0x00050000 | 1) ? false : throw new InvalidOperationException("unexpected return " + instruction.K);
                default:
                    throw new InvalidOperationException("unexpected instruction " + instruction.Code);
            }
        }
    }

    private static (T Value, string? Status) RunOnThread<T>(bool filtered, Func<T> body)
    {
        T value = default!;
        string? status = null;
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                if (filtered)
                {
                    Assert.True(ProcessHardening.SetNoNewPrivs());
                    status = Restrict(allThreads: false);
                    if (!status.Contains("only AF_UNIX", StringComparison.Ordinal))
                    {
                        return;
                    }
                }

                value = body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("the probe thread failed", failure);
        }

        return (value, status);
    }

    /// <summary>The raw system call: its result, or minus its errno.</summary>
    private static long Probe(long number, long a, long b, long c)
    {
        var result = Syscall(number, a, b, c, 0, 0);
        return result < 0 ? -Marshal.GetLastPInvokeError() : result;
    }

    /// <summary>
    /// A socket the probe got is closed again. Nothing here wants it, and without close-on-exec (a raw call sets none) it would stay open for the rest of the run in a process that starts children: six
    /// sockets that showed up in every child the sandbox tests started.
    /// </summary>
    private static long ProbeSocket(long number, long domain, long type, long protocol)
    {
        var result = Probe(number, domain, type, protocol);
        if (result >= 0)
        {
            _ = Close((int)result);
        }

        return result;
    }

    private static long ProbePair(long number)
    {
        var pair = new int[2];
        var result = SyscallPair(number, 1, 1, 0, pair);
        if (result >= 0)
        {
            _ = Close(pair[0]);
            _ = Close(pair[1]);
        }

        return result < 0 ? -Marshal.GetLastPInvokeError() : result;
    }

    private static long ProbeIoUring(long number)
    {
        var result = Syscall(number, 1, 0, 0, 0, 0);
        return result < 0 ? -Marshal.GetLastPInvokeError() : result;
    }

    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int fd);

    [DllImport("libc", EntryPoint = "pipe")]
    private static extern int Pipe(int[] fds);

    [DllImport("libc", EntryPoint = "gettid")]
    private static extern int Gettid();

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long Syscall(long number, long a, long b, long c, long d, long e);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long SyscallPair(long number, long domain, long type, long protocol, int[] pair);
}
