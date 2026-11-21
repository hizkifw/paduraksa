using Paduraksa.Proxy.Gate;

namespace Paduraksa.Proxy.Honeypot;

/// <summary>
/// A host's trap as the gate uses it, built once per snapshot. The prefix is compared, ordinally, with the canonical path of every
/// request of the host, so everything else a trap answer needs is looked up only after that compare hits.
/// </summary>
internal sealed class CompiledHoneypot
{
    public CompiledHoneypot(
        string prefix,
        IHoneypotSite? site,
        bool noIndex,
        int powDifficulty,
        int powShare,
        bool serveRobotsLine,
        HoneypotRuntime runtime,
        ProofOfWorkRuntime proofOfWork)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(proofOfWork);
        Runtime = runtime;
        ProofOfWork = proofOfWork;
        Prefix = prefix;
        Site = site;
        NoIndex = noIndex;
        PowDifficulty = powDifficulty;
        PowShare = powShare;
        EntryByPrefix = serveRobotsLine ? HoneypotEntry.Robots : HoneypotEntry.Prefix;
        VerifyPath = prefix + VerifyName;
        Page = site is not null && powShare > 0 ? new ProofOfWorkPage(VerifyPath, powDifficulty) : null;
    }

    /// <summary>The path of the proof-of-work verify endpoint, under the prefix, so a trap token's cookie (whose path is the prefix) goes with it.</summary>
    public const string VerifyName = "verify";

    /// <summary>The sink, the counters and the cap: the process's, carried here so the gate's decision needs no more than the host.</summary>
    public HoneypotRuntime Runtime { get; }

    /// <summary>The key, the clock and the process counters of the proof-of-work flow the trap's challenge shares.</summary>
    public ProofOfWorkRuntime ProofOfWork { get; }

    /// <summary>The trap prefix: canonical, starting and ending with <c>/</c>.</summary>
    public string Prefix { get; }

    /// <summary>The site; null when the key that all of it comes from couldn't be opened, which makes the trap answer 503.</summary>
    public IHoneypotSite? Site { get; }

    public bool NoIndex { get; }

    public int PowDifficulty { get; }

    public int PowShare { get; }

    /// <summary>Robots when the host serves the Disallow line (a request under the prefix broke robots.txt), else Prefix.</summary>
    public HoneypotEntry EntryByPrefix { get; }

    public string VerifyPath { get; }

    /// <summary>The challenge page; null when no page is ever behind a challenge.</summary>
    public ProofOfWorkPage? Page { get; }
}
