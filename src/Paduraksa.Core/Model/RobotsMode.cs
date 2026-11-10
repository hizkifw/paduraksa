using System.Text.Json.Serialization;

using Paduraksa.Core.Json;

namespace Paduraksa.Core.Model;

/// <summary>What a host serves at <c>/robots.txt</c>.</summary>
[JsonConverter(typeof(ConfigEnumConverter<RobotsMode>))]
public enum RobotsMode
{
    /// <summary>Forward the request to the upstream, which answers with its own file (the built-in default).</summary>
    Passthrough,

    /// <summary>The built-in <c>User-agent: * / Disallow: /</c>.</summary>
    DisallowAll,

    /// <summary>Disallow the AI crawlers this release knows about, and allow every other crawler.</summary>
    BlockAiCrawlers,

    /// <summary>The operator's own text.</summary>
    Custom,
}
