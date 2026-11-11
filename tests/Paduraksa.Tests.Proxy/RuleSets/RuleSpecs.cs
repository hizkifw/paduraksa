using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;

namespace Paduraksa.Tests.Proxy.RuleSets;

internal static class RuleSpecs
{
    public static RuleSpec Rule(
        RuleAction action,
        RulePathKind? kind = null,
        string? pattern = null,
        string[]? methods = null,
        string[]? agents = null,
        bool emptyAgent = false) => new()
        {
            Action = action,
            PathKind = kind,
            Pattern = pattern,
            Methods = methods ?? [],
            UserAgentContains = agents ?? [],
            UserAgentEmpty = emptyAgent,
        };
}
