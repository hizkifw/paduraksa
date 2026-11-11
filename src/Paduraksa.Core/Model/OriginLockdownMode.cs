using System.Text.Json.Serialization;

using Paduraksa.Core.Json;

namespace Paduraksa.Core.Model;

/// <summary>What a host does with a request whose connection doesn't come from Cloudflare (or from <c>cloudflared</c>).</summary>
[JsonConverter(typeof(ConfigEnumConverter<OriginLockdownMode>))]
public enum OriginLockdownMode
{
    /// <summary>Any peer may reach the host (the built-in default).</summary>
    Off,

    /// <summary>Answer 403 with the built-in error page.</summary>
    Forbid,

    /// <summary>Abort the connection without an answer.</summary>
    Drop,
}
