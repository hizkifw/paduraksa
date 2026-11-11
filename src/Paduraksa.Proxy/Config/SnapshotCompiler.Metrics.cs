using Paduraksa.Core.Model;
using Paduraksa.Proxy.Metrics;

namespace Paduraksa.Proxy.Config;

/// <summary>The metric slots of the compiled hosts, routes, upstreams and destinations (PLAN §6.3, §7.4).</summary>
public static partial class SnapshotCompiler
{
    internal sealed partial class CompileContext
    {
        private readonly Dictionary<UpstreamId, UpstreamMetrics?> _upstreamMetrics = [];

        public MetricRegistrations MetricRegistrations { get; } = new();

        /// <summary>
        /// The object's stable slot, or none when the compile has no registry, the object is disabled (a disabled host serves
        /// nothing, so it owns no counters) or it has no usable ID. Only a compile that will be published allocates; any
        /// other only finds the slot an earlier publish gave the object.
        /// </summary>
        public MetricSlot MetricSlotOf(MetricSubject subject, Guid id, bool enabled)
        {
            if (Options.Metrics is not { } metrics || !enabled || id == Guid.Empty)
            {
                return MetricSlot.None;
            }

            MetricSlot slot;
            if (Options.WillBePublished)
            {
                try
                {
                    slot = metrics.SlotFor(subject, id);
                }
                catch (InvalidOperationException ex)
                {
                    // Every slot is taken and none is free: a compile error says so, rather than throwing through the pipeline.
                    Errors.Add(new(CompileErrorSource.Defaults, ex.Message));
                    return MetricSlot.None;
                }
            }
            else
            {
                slot = metrics.FindSlot(subject, id);
            }

            if (!slot.IsNone)
            {
                MetricRegistrations.Add(subject, id, slot.Index);
            }

            return slot;
        }

        /// <summary>The upstream's slot and its destinations', shared by every route that forwards to it; null for an unknown upstream or one nobody counts.</summary>
        public UpstreamMetrics? UpstreamMetricsOf(UpstreamId upstream, bool enabled)
        {
            if (Options.Metrics is null || !enabled || upstream.IsEmpty)
            {
                return null;
            }

            if (_upstreamMetrics.TryGetValue(upstream, out var existing))
            {
                return existing;
            }

            UpstreamMetrics? compiled = null;
            if (Clusters.TryGetValue(upstream, out var cluster))
            {
                var slot = MetricSlotOf(MetricSubject.Upstream, upstream.Value, enabled);
                if (!slot.IsNone)
                {
                    List<string> keys = [];
                    List<MetricSlot> slots = [];
                    foreach (var spec in Model.Upstreams ?? [])
                    {
                        if (spec.Id != upstream)
                        {
                            continue;
                        }

                        foreach (var destination in spec.Destinations)
                        {
                            var key = destination.Id.ToString();
                            if (cluster.Destinations?.ContainsKey(key) == true)
                            {
                                keys.Add(key);
                                slots.Add(MetricSlotOf(MetricSubject.Destination, destination.Id.Value, enabled));
                            }
                        }

                        break;
                    }

                    compiled = new UpstreamMetrics(upstream, slot, keys.ToArray(), slots.ToArray());
                }
            }

            _upstreamMetrics[upstream] = compiled;
            return compiled;
        }
    }
}
