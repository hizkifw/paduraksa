using System.Buffers;
using System.Text;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Honeypot;
using Paduraksa.ProxyProtocol;

namespace Paduraksa.Tests.Proxy.Testing;

/// <summary>
/// A small trap site for the gate's tests: the page kinds the URL says, so a test chooses what it asks the gate for. The real
/// generator is tested on its own branch; the gate only needs a site that obeys the interface.
/// </summary>
/// <remarks>
/// Under the prefix: <c>""</c> is the index, <c>post-N/</c> a post (<c>N</c> its page ID), <c>secure-N/</c> a post behind
/// the challenge, <c>image.svg</c> an image and <c>beacon</c> a beacon; anything else is a page that isn't there.
/// </remarks>
internal sealed class FakeHoneypotSite(string prefix) : IHoneypotSite
{
    public static readonly DateTimeOffset Modified = new(2024, 7, 9, 10, 0, 0, TimeSpan.Zero);

    public string Prefix { get; } = prefix;

    public int Renders { get; private set; }

    public Action<HoneypotTarget>? OnRender { get; init; }

    public HoneypotTarget Resolve(ReadOnlySpan<char> canonicalPath)
    {
        if (!canonicalPath.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return new(HoneypotPageKind.NotFound, HoneypotLinkKind.Direct, 0, 0, false);
        }

        var rest = canonicalPath[Prefix.Length..];
        if (rest.IsEmpty)
        {
            return new(HoneypotPageKind.Index, HoneypotLinkKind.Direct, 1, 0, false);
        }

        if (rest.StartsWith("post-", StringComparison.Ordinal) && ulong.TryParse(rest[5..^1], out var post))
        {
            return new(HoneypotPageKind.Post, HoneypotLinkKind.Normal, post, 1, false);
        }

        if (rest.StartsWith("hidden-", StringComparison.Ordinal) && ulong.TryParse(rest[7..^1], out var hidden))
        {
            return new(HoneypotPageKind.Post, HoneypotLinkKind.Hidden, hidden, 1, false);
        }

        if (rest.StartsWith("secure-", StringComparison.Ordinal) && ulong.TryParse(rest[7..^1], out var secure))
        {
            return new(HoneypotPageKind.Post, HoneypotLinkKind.Normal, secure, 1, true);
        }

        return rest switch
        {
            "image.svg" => new(HoneypotPageKind.Image, HoneypotLinkKind.Image, 7, 1, false),
            "beacon" => new(HoneypotPageKind.Beacon, HoneypotLinkKind.Beacon, 8, 1, false),
            _ => new(HoneypotPageKind.NotFound, HoneypotLinkKind.Direct, 0, 0, false),
        };
    }

    public HoneypotTarget Landing(ReadOnlySpan<char> canonicalPath) => new(HoneypotPageKind.Landing, HoneypotLinkKind.Direct, 5, 0, false);

    public HoneypotRender Render(in HoneypotTarget target, ReadOnlySpan<char> canary, IBufferWriter<byte> output)
    {
        Renders++;
        OnRender?.Invoke(target);
        var body = Encoding.UTF8.GetBytes($"<html><body>{target.Kind} {target.PageId} canary={canary}</body></html>");
        body.CopyTo(output.GetSpan(body.Length));
        output.Advance(body.Length);
        return target.Kind switch
        {
            HoneypotPageKind.NotFound => new HoneypotRender(404, "text/html; charset=utf-8", null),
            HoneypotPageKind.Image => new HoneypotRender(200, "image/svg+xml", Modified),
            _ => new HoneypotRender(200, "text/html; charset=utf-8", Modified),
        };
    }

    public string Canary(ReadOnlySpan<byte> clientIp, ReadOnlySpan<char> userAgent) =>
        string.Concat("canary-", Convert.ToHexString(clientIp[^2..]), "-", userAgent.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>Builds a <see cref="FakeHoneypotSite"/> for each host and keeps what it was asked for.</summary>
internal sealed class FakeHoneypotSiteFactory : IHoneypotSiteFactory
{
    public List<(byte[] Key, HoneypotSiteOptions Options)> Created { get; } = [];

    /// <summary>Called by every site it makes as it renders, so a test can hold a render open.</summary>
    public Action<HoneypotTarget>? OnRender { get; set; }

    public string DerivePrefix(ReadOnlySpan<byte> siteKey) => "/archive-" + Convert.ToHexStringLower(siteKey[..2]) + "/";

    public string DeriveSiteName(ReadOnlySpan<byte> siteKey) => "Blog " + Convert.ToHexStringLower(siteKey[..2]);

    public IHoneypotSite Create(ReadOnlySpan<byte> siteKey, HoneypotSiteOptions options)
    {
        Created.Add((siteKey.ToArray(), options));
        return new FakeHoneypotSite(options.Prefix) { OnRender = target => OnRender?.Invoke(target) };
    }
}

/// <summary>Keeps every hit and robots fetch it is handed.</summary>
internal sealed class RecordingHoneypotSink : IHoneypotSink
{
    public List<HoneypotHit> Hits { get; } = [];

    public List<(HostId Host, IpKey Client, string UserAgent)> RobotsFetches { get; } = [];

    public void Record(in HoneypotHit hit) => Hits.Add(hit);

    public void RobotsFetched(HostId host, IpKey clientIp, string userAgent) => RobotsFetches.Add((host, clientIp, userAgent));
}

/// <summary>The process-level pieces a trap test needs: an open proof-of-work key, a sink, a fake factory and the compiler options that carry them.</summary>
internal sealed class GateTrapRig
{
    public GateTrapRig(int maxConcurrent = HoneypotLimits.DefaultMaxConcurrent, bool withKey = true, TimeProvider? time = null)
    {
        Time = time ?? TimeProvider.System;
        ProofOfWork = new ProofOfWorkRuntime(Time);
        if (withKey)
        {
            ProofOfWork.UseKey(new TestMacKey());
        }

        Honeypot = new HoneypotRuntime(Sink, Factory, maxConcurrent, Time);
        Options = TestSnapshots.Port8443 with { ProofOfWork = ProofOfWork, Honeypot = Honeypot };
    }

    public TimeProvider Time { get; }

    public RecordingHoneypotSink Sink { get; } = new();

    public FakeHoneypotSiteFactory Factory { get; } = new();

    public ProofOfWorkRuntime ProofOfWork { get; }

    public HoneypotRuntime Honeypot { get; }

    public SnapshotCompilerOptions Options { get; }
}
