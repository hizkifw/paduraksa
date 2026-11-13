using System.Text.Json.Serialization;

namespace Paduraksa.Contracts.Api;

/// <summary>What is running: the product's version and the commit it was built from.</summary>
/// <param name="Version">The product version (SemVer), for example <c>0.1.0</c>.</param>
/// <param name="Api">The API's major version, as in its path prefix: <c>v1</c>.</param>
/// <param name="Commit">The full SHA of the commit the build came from; null for a build that was not given one (the container image build passes it as <c>-p:SourceRevisionId</c>). Always written.</param>
public sealed record VersionInfo(string Version, string Api, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Commit = null);
