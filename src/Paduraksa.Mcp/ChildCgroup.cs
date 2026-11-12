using System.Globalization;

namespace Paduraksa.Mcp;

/// <summary>
/// Puts a child in a cgroup v2 of its own with <c>memory.max</c>, <c>cpu.max</c> and <c>pids.max</c>, when this process may create one (a delegated systemd
/// scope, a writable cgroup file system). Where it may not (Docker's read-only cgroup file system, no cgroup v2), nothing is done and the parent's own limits
/// are all there is: <see cref="Probe"/> says which, once.
/// </summary>
internal sealed class ChildCgroup
{
    private readonly string _root;
    private int _next;

    private ChildCgroup(string root) => _root = root;

    /// <summary>
    /// cgroup v2 allows controllers to be enabled for the children of a cgroup only while the cgroup itself has no processes ("no internal processes"), and the
    /// server is a process of the cgroup it was started in. So the standard delegated layout is made: the server moves into a leaf of its own cgroup
    /// (<c>paduraksa-server</c>), the controllers are enabled on the cgroup it left, and the children go in leaves of a sibling (<c>paduraksa-mcp/child-N</c>). Where
    /// any step is refused (nothing delegated, a read-only file system) the server is put back and the answer says which refusal it was.
    /// </summary>
    public static (ChildCgroup? Cgroup, string Status) Probe()
    {
        if (!OperatingSystem.IsLinux())
        {
            return (null, "not Linux");
        }

        string? own;
        try
        {
            own = File.ReadLines("/proc/self/cgroup").FirstOrDefault(l => l.StartsWith("0::", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, "no cgroup v2: " + Describe("reading /proc/self/cgroup", ex));
        }

        if (own is null)
        {
            return (null, "no cgroup v2");
        }

        var directory = "/sys/fs/cgroup" + own[3..].TrimEnd();
        var pid = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        var leaf = Path.Combine(directory, "paduraksa-server");
        var moved = false;
        var step = "creating " + leaf;
        try
        {
            Directory.CreateDirectory(leaf);
            step = "moving the server into " + leaf;
            File.WriteAllText(Path.Combine(leaf, "cgroup.procs"), pid);
            moved = true;
            step = "enabling controllers on " + directory;
            File.WriteAllText(Path.Combine(directory, "cgroup.subtree_control"), "+memory +cpu +pids");
            var root = Path.Combine(directory, "paduraksa-mcp");
            step = "creating " + root;
            Directory.CreateDirectory(root);
            step = "enabling controllers on " + root;
            File.WriteAllText(Path.Combine(root, "cgroup.subtree_control"), "+memory +cpu +pids");
            return File.Exists(Path.Combine(root, "cgroup.procs")) ? (new ChildCgroup(root), "cgroup v2 at " + root) : (null, "cgroup v2 not usable: no cgroup.procs in " + root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (moved)
            {
                try
                {
                    File.WriteAllText(Path.Combine(directory, "cgroup.procs"), pid);
                }
                catch (Exception back) when (back is IOException or UnauthorizedAccessException)
                {
                    // stays in its leaf, which is harmless: it is still inside the cgroup it was started in
                }
            }

            Remove(leaf);

            return (null, "cgroup limits not used: " + Describe(step, ex));
        }
    }

    /// <summary>Names the refusal: the kernel's error behind a file operation on the cgroup file system, so that a log line says what to change.</summary>
    internal static string Describe(string step, Exception exception) => exception switch
    {
        UnauthorizedAccessException => $"{step}: EACCES (this user may not change it; run the server in a delegated cgroup, e.g. a systemd service with Delegate=yes)",
        IOException io when io.Message.Contains("read-only", StringComparison.OrdinalIgnoreCase) => $"{step}: EROFS (the cgroup file system is read-only, as in a default container)",
        IOException io when io.Message.Contains("busy", StringComparison.OrdinalIgnoreCase) => $"{step}: EBUSY (the cgroup still has processes, or controllers are enabled in a parent that has some)",
        _ => $"{step}: {exception.GetType().Name}",
    };

    /// <summary>Moves <paramref name="pid"/> into a new cgroup with the limits; returns where, so it can be removed.</summary>
    public string? Place(int pid, long memoryBytes, int cpuPercent, int maxPids)
    {
        try
        {
            var path = Path.Combine(_root, "child-" + Interlocked.Increment(ref _next).ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "memory.max"), memoryBytes.ToString(CultureInfo.InvariantCulture));
            File.WriteAllText(Path.Combine(path, "cpu.max"), $"{cpuPercent * 1000} 100000");
            File.WriteAllText(Path.Combine(path, "pids.max"), maxPids.ToString(CultureInfo.InvariantCulture));
            File.WriteAllText(Path.Combine(path, "cgroup.procs"), pid.ToString(CultureInfo.InvariantCulture));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Remove(string path)
    {
        try
        {
            Directory.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // still has a process that is dying; the next sweep of the directory removes it
        }
    }
}
