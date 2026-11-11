using Paduraksa.Core.Model;
using Paduraksa.Proxy.RuleSets;

namespace Paduraksa.Proxy.Config;

/// <summary>The named rule sets, compiled once per snapshot and shared by every policy that names one.</summary>
public static partial class SnapshotCompiler
{
    /// <summary>What a policy needs to bind a <see cref="RuleSetPolicySpec"/>: the compiled sets that compiled.</summary>
    internal sealed record RuleSetBinding(Dictionary<RuleSetId, CompiledRuleSet> Sets);

    private static RuleSetBinding CompileRuleSets(IReadOnlyList<RuleSetSpec> specs, RuleSetRuntime runtime, List<CompileError> errors)
    {
        Dictionary<RuleSetId, CompiledRuleSet> sets = [];
        foreach (var spec in specs)
        {
            if (sets.ContainsKey(spec.Id))
            {
                continue;
            }

            List<string> problems = [];
            if (CompiledRuleSet.TryCreate(spec, runtime.CountersFor(spec.Id), problems) is { } compiled)
            {
                sets.Add(spec.Id, compiled);
                continue;
            }

            var where = CompileErrorSource.RuleSet(spec.Id);
            foreach (var problem in problems)
            {
                errors.Add(new CompileError(where, problem));
            }
        }

        return new RuleSetBinding(sets);
    }

    internal sealed partial class CompileContext
    {
        /// <summary>The compiled set <paramref name="id"/> names, or null for none (a missing one is the policies' compile error).</summary>
        public CompiledRuleSet? RuleSetOf(RuleSetId? id) =>
            id is { } named && RuleSets.Sets.TryGetValue(named, out var set) ? set : null;
    }
}
