// Linked into Paduraksa.Tests.Proxy and Paduraksa.Tests.Integration (<Compile Include Link>), so it compiles into each
// test assembly as an internal type of its own.
using System.Security.Cryptography;
using System.Text;

using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;

namespace Paduraksa.Tests.Shared;

/// <summary>
/// Typed IDs named by a readable label, so a config reads <c>TestIds.Host("app")</c> instead of an opaque
/// <c>host_…</c>. The ID is a hash of its prefix and the label: the same label always gives the same ID (across runs
/// too), different labels give different IDs, and labels that differ only in case are different labels.
/// These aren't valid UUIDv7 values and their order means nothing (sort them only to compare collections).
/// </summary>
internal static class TestIds
{
    public static HostId Host(string label) => Of<HostId>(label);

    public static RouteId Route(string label) => Of<RouteId>(label);

    public static UpstreamId Upstream(string label) => Of<UpstreamId>(label);

    public static DestinationId Destination(string label) => Of<DestinationId>(label);

    public static CertificateId Certificate(string label) => Of<CertificateId>(label);

    public static ConfigRevisionId Revision(string label) => Of<ConfigRevisionId>(label);

    /// <summary>
    /// A different ID whose string form equals <paramref name="id"/>'s ignoring case: the first letter after the
    /// prefix has its case flipped. YARP, which compares IDs ignoring case, would take both for one ID.
    /// </summary>
    public static TId CaseTwin<TId>(TId id)
        where TId : struct, ITypedId<TId>
    {
        char[] chars = id.ToString()!.ToCharArray();
        int letter = Array.FindIndex(chars, TId.Prefix.Length + 1, char.IsAsciiLetter);
        if (letter < 0)
        {
            throw new ArgumentException($"{id} has no letter to flip.", nameof(id));
        }

        chars[letter] = char.IsAsciiLetterUpper(chars[letter]) ? char.ToLowerInvariant(chars[letter]) : char.ToUpperInvariant(chars[letter]);
        return TId.Parse(chars);
    }

    private static TId Of<TId>(string label)
        where TId : struct, ITypedId<TId, Guid>
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes($"{TId.Prefix}:{label}"), hash);
        return TId.FromValue(new Guid(hash[..16]));
    }
}
