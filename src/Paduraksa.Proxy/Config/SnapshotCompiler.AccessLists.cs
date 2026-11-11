using Paduraksa.Core.Model;
using Paduraksa.Core.Security;
using Paduraksa.Core.Text;
using Paduraksa.Proxy.AccessLists;
using Paduraksa.Proxy.Net;

namespace Paduraksa.Proxy.Config;

/// <summary>The named access lists, compiled once per snapshot and shared by every policy that names one.</summary>
public static partial class SnapshotCompiler
{
    /// <summary>What a policy needs to bind an <see cref="AccessListPolicySpec"/>: the compiled lists and the runtime.</summary>
    internal sealed record AccessListBinding(Dictionary<AccessListId, CompiledAccessList> Lists, AccessListRuntime Runtime);

    private static AccessListBinding CompileAccessLists(IReadOnlyList<AccessListSpec> specs, AccessListRuntime runtime, List<CompileError> errors)
    {
        Dictionary<AccessListId, CompiledAccessList> lists = [];
        foreach (var spec in specs)
        {
            if (lists.ContainsKey(spec.Id))
            {
                continue;
            }

            var where = CompileErrorSource.AccessList(spec.Id);
            var allow = CompileCidrs(spec.Allow ?? [], where, errors);
            var deny = CompileCidrs(spec.Deny ?? [], where, errors);
            if (!Enum.IsDefined(spec.Satisfy))
            {
                errors.Add(new(where, $"unknown satisfy mode {spec.Satisfy}"));
            }

            var realm = spec.Realm ?? string.Empty;
            if (realm.Length is 0 or > AccessListLimits.MaxRealmLength || realm.AsSpan().ContainsAnyExcept(AsciiChars.Printable))
            {
                errors.Add(new(where, $"the realm must be 1-{AccessListLimits.MaxRealmLength} printable ASCII characters"));
                realm = spec.Name;
            }

            List<CompiledAccessListUser> users = [];
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (var user in spec.Users ?? [])
            {
                if (user.Username is not { Length: > 0 and <= AccessListLimits.MaxUsernameLength } name
                    || name.AsSpan().ContainsAnyExcept(AsciiChars.UsernameChars))
                {
                    errors.Add(new(where, $"a username must be 1-{AccessListLimits.MaxUsernameLength} visible ASCII characters, without ':'"));
                }
                else if (!seen.Add(name))
                {
                    errors.Add(new(where, $"the username '{name}' is listed twice"));
                }
                else if (!PasswordHash.TryParse(user.PasswordHash, out var parsed))
                {
                    errors.Add(new(where, $"the password hash of '{name}' isn't one this build reads"));
                }
                else
                {
                    users.Add(new CompiledAccessListUser(spec.Id, name, user.PasswordHash, parsed));
                }
            }

            lists.Add(spec.Id, new CompiledAccessList(
                spec.Id,
                spec.Name,
                allow,
                deny,
                Networks(spec.Allow ?? []),
                Networks(spec.Deny ?? []),
                spec.DropDenied,
                spec.Satisfy,
                spec.PassCredentialsUpstream,
                realm,
                [.. users]));
        }

        return new AccessListBinding(lists, runtime);
    }

    private static bool TryBindUnion(AccessListUnionPolicySpec union, AccessListBinding binding, out CompiledAccessList[] lists)
    {
        lists = new CompiledAccessList[union.Lists.Count];
        for (var i = 0; i < lists.Length; i++)
        {
            if (!binding.Lists.TryGetValue(union.Lists[i], out var list))
            {
                return false;
            }

            lists[i] = list;
        }

        return lists.Length > 0;
    }

    private static IpNetwork[] Networks(IReadOnlyList<string> blocks)
    {
        List<IpNetwork> networks = new(blocks.Count);
        foreach (var block in blocks)
        {
            if (IpNetwork.TryParse(block ?? string.Empty, out var network))
            {
                networks.Add(network);
            }
        }

        return [.. networks];
    }

    internal sealed partial class CompileContext
    {
        /// <summary>The compiled list <paramref name="id"/> names, or null for none (a missing one is the policies' compile error).</summary>
        public CompiledAccessList? AccessListOf(AccessListId? id) =>
            id is { } named && AccessLists.Lists.TryGetValue(named, out var list) ? list : null;
    }
}
