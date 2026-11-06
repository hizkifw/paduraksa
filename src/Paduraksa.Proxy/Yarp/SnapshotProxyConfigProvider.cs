using Microsoft.Extensions.Primitives;

using Yarp.ReverseProxy.Configuration;

namespace Paduraksa.Proxy.Yarp;

/// <summary>
/// The only <see cref="IProxyConfigProvider"/> of the public app (PLAN §6.1 step 6a). Each update wraps the content
/// in a new <see cref="IProxyConfig"/> with its own change token and a unique <see cref="IProxyConfig.RevisionId"/>,
/// swaps it in, then cancels the previous token so YARP reloads. YARP itself never reads <c>RevisionId</c>; it's
/// the key that correlates <see cref="IConfigChangeListener"/> callbacks with the publish that caused them.
/// <para>Only <see cref="SnapshotPublisher"/> updates it (single writer).</para>
/// </summary>
internal sealed class SnapshotProxyConfigProvider : IProxyConfigProvider
{
    private volatile AppliedConfig _current = new(YarpConfig.Empty, "initial");

    /// <summary>The config YARP reads next (and, once applied, the one it runs).</summary>
    public IProxyConfig GetConfig() => _current;

    /// <summary>The content of <see cref="GetConfig"/>.</summary>
    public YarpConfig CurrentContent => _current.Content;

    /// <summary>
    /// Swaps in <paramref name="content"/> under <paramref name="revisionId"/> (unique per update) and signals YARP.
    /// YARP may reload inline, inside this call (spike S2 Q5), so register for the outcome first.
    /// </summary>
    internal void Update(YarpConfig content, string revisionId)
    {
        AppliedConfig previous = Interlocked.Exchange(ref _current, new AppliedConfig(content, revisionId));
        previous.Expire();
    }

#pragma warning disable CA1001 // The token source is never disposed on purpose (see Expire).
    private sealed class AppliedConfig : IProxyConfig
#pragma warning restore CA1001
    {
        private readonly CancellationTokenSource _expired = new();

        public AppliedConfig(YarpConfig content, string revisionId)
        {
            Content = content;
            RevisionId = revisionId;
            ChangeToken = new CancellationChangeToken(_expired.Token);
        }

        public YarpConfig Content { get; }

        public string RevisionId { get; }

        public IReadOnlyList<RouteConfig> Routes => Content.Routes;

        public IReadOnlyList<ClusterConfig> Clusters => Content.Clusters;

        public IChangeToken ChangeToken { get; }

        // The source is never disposed: YARP may still register on the token after it fired.
        public void Expire() => _expired.Cancel();
    }
}
