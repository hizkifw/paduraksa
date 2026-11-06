using System.Buffers;
using System.Runtime.CompilerServices;

namespace S3;

public enum PolicyAction : byte { Continue, Respond, Challenge, Drop }

/// <summary>16 bytes: a kind plus a reference to a precomputed response. Returned by value.</summary>
public readonly struct PolicyDecision
{
    public readonly PrecomputedResponse? Response;
    public readonly PolicyAction Action;

    private PolicyDecision(PolicyAction a, PrecomputedResponse? r) { Action = a; Response = r; }
    public static PolicyDecision Continue => default;
    public static PolicyDecision Respond(PrecomputedResponse r) => new(PolicyAction.Respond, r);
    public static PolicyDecision Drop => new(PolicyAction.Drop, null);
}

/// <summary>What a policy sees. Passed by `in` so it costs a pointer.</summary>
public readonly ref struct RequestView
{
    public readonly HttpContext Http;
    public readonly GateRequestState State;
    public RequestView(HttpContext http, GateRequestState state) { Http = http; State = state; }
}

public interface IRequestPolicy
{
    PolicyDecision Evaluate(in RequestView r);
}

// ---- IP CIDR deny: sorted, merged, non-overlapping [start, end] ranges over UInt128, binary search ----
public sealed class IpRangePolicy : IRequestPolicy
{
    private readonly UInt128[] _start, _end;
    private readonly PrecomputedResponse _deny;

    public IpRangePolicy(IEnumerable<(UInt128 Net, int Prefix)> cidrs, PrecomputedResponse deny)
    {
        var ranges = cidrs.Select(c =>
        {
            var host = c.Prefix == 128 ? UInt128.Zero : (UInt128.MaxValue >> c.Prefix);
            var s = c.Net & ~host;
            return (s, s | host);
        }).OrderBy(r => r.s).ToList();
        var merged = new List<(UInt128 s, UInt128 e)>();
        foreach (var r in ranges)
        {
            if (merged.Count > 0 && r.s <= merged[^1].e + 1) merged[^1] = (merged[^1].s, UInt128.Max(merged[^1].e, r.Item2));
            else merged.Add((r.s, r.Item2));
        }
        _start = merged.Select(m => m.s).ToArray();
        _end = merged.Select(m => m.e).ToArray();
        _deny = deny;
    }

    public int Count => _start.Length;

    public bool Contains(UInt128 ip)
    {
        // last start <= ip
        int lo = 0, hi = _start.Length - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_start[mid] <= ip) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found >= 0 && ip <= _end[found];
    }

    public PolicyDecision Evaluate(in RequestView r) => Contains(r.State.ClientIp) ? PolicyDecision.Respond(_deny) : PolicyDecision.Continue;
}

// ---- User-agent deny: SearchValues<string> multi-substring, case-insensitive ----
public sealed class UserAgentPolicy : IRequestPolicy
{
    private readonly SearchValues<string> _needles;
    private readonly PrecomputedResponse _deny;
    private readonly bool _denyEmpty;

    public UserAgentPolicy(string[] needles, bool denyEmpty, PrecomputedResponse deny)
    {
        _needles = SearchValues.Create(needles, StringComparison.OrdinalIgnoreCase);
        _denyEmpty = denyEmpty;
        _deny = deny;
    }

    public bool Matches(ReadOnlySpan<char> ua) => ua.IsEmpty ? _denyEmpty : ua.ContainsAny(_needles);

    public PolicyDecision Evaluate(in RequestView r)
    {
        // Known header: Kestrel stores it in a field; single value → ToString() returns the existing string.
        var ua = r.Http.Request.Headers.UserAgent;
        return Matches(ua.Count == 1 ? ua[0].AsSpan() : ua.ToString().AsSpan()) ? PolicyDecision.Respond(_deny) : PolicyDecision.Continue;
    }
}

// ---- Path prefix deny (ordinal-ignore-case, segment-aware) ----
public sealed class PathPrefixPolicy : IRequestPolicy
{
    private readonly string[] _prefixes;
    private readonly PrecomputedResponse _deny;

    public PathPrefixPolicy(string[] prefixes, PrecomputedResponse deny) { _prefixes = prefixes; _deny = deny; }

    public static bool SegmentPrefix(ReadOnlySpan<char> path, string prefix) =>
        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (path.Length == prefix.Length || path[prefix.Length] == '/' || prefix.EndsWith('/'));

    public PolicyDecision Evaluate(in RequestView r)
    {
        var path = r.Http.Request.Path.Value.AsSpan();
        foreach (var p in _prefixes)
            if (SegmentPrefix(path, p)) return PolicyDecision.Respond(_deny);
        return PolicyDecision.Continue;
    }
}

// =============================================================================================
// Dispatch-shape comparison (benchmark only): same three checks as interface / abstract class / enum switch.
// The work is a trivial compare so the dispatch cost dominates.
// =============================================================================================

public interface IDispatchProbe { int Eval(int x); }
public sealed class ProbeA : IDispatchProbe { public int K = 3; public int Eval(int x) => x == K ? 1 : 0; }
public sealed class ProbeB : IDispatchProbe { public int K = 5; public int Eval(int x) => x > K * 1000 ? 1 : 0; }
public sealed class ProbeC : IDispatchProbe { public int K = 7; public int Eval(int x) => (x & K) == 99 ? 1 : 0; }

public abstract class ProbeBase { public abstract int Eval(int x); }
public sealed class ProbeA2 : ProbeBase { public int K = 3; public override int Eval(int x) => x == K ? 1 : 0; }
public sealed class ProbeB2 : ProbeBase { public int K = 5; public override int Eval(int x) => x > K * 1000 ? 1 : 0; }
public sealed class ProbeC2 : ProbeBase { public int K = 7; public override int Eval(int x) => (x & K) == 99 ? 1 : 0; }

public enum ProbeKind : byte { A, B, C }
public sealed class ProbeRecord
{
    public ProbeKind Kind; public int K;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Eval(int x) => Kind switch
    {
        ProbeKind.A => x == K ? 1 : 0,
        ProbeKind.B => x > K * 1000 ? 1 : 0,
        _ => (x & K) == 99 ? 1 : 0,
    };
}
