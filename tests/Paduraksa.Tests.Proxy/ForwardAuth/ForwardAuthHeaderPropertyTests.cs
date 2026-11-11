using System.Net.Http.Headers;

using CsCheck;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.ForwardAuth;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.ForwardAuth.ForwardAuthFixtures;

namespace Paduraksa.Tests.Proxy.ForwardAuth;

/// <summary>
/// The headers a provider owns: what the proxy strips from a client's request is what a plain model says, with '_' and '-' one
/// character and case ignored; and a name the validator lets a provider own can never strip one of the headers the proxy
/// owns itself (framing and forwarding headers, the client's credentials).
/// </summary>
public sealed class ForwardAuthHeaderPropertyTests
{
    // What the proxy owns and a provider may neither be sent a client's copy of nor replace: the validator's reserved list, the
    // X-Forwarded-* set and the client's credentials.
    private static readonly string[] ProtectedNames =
    [
        "Connection", "Content-Length", "Content-Encoding", "Forwarded", "Host", "Keep-Alive", "Proxy-Connection", "TE", "Trailer", "Transfer-Encoding", "Upgrade",
        "X-Real-IP", "X-Request-Id", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Forwarded-Uri", "X-Forwarded-Method", "X-Forwarded-Port",
        "Authorization", "Cookie", "Proxy-Authorization",
    ];

    private static readonly string[] NamePieces =
    [
        "X", "-", "_", "-", "Forwarded", "Remote", "User", "Auth", "Authorization", "Cookie", "Host", "Content", "Length", "Real", "IP", "Request", "Id", "Proxy", "Connection", "Trailer",
        "TE", "Te", "Transfer", "Encoding", "Upgrade", "Keep", "Alive", "Groups", "Email", "x", "co", "*", "a", "1", "#",
    ];

    private static readonly Gen<string> Names = Gen.Int[0, NamePieces.Length - 1].Select(i => NamePieces[i]).List[1, 5]
        .Select(static parts => string.Concat(parts));

    // The ways a client can spell a protected name: its case and its separators.
    private static readonly Gen<string> Spellings = Gen.Select(Property.Pick(ProtectedNames), Gen.Bool, Gen.Int[0, 3]).Select(static (name, underscores, casing) =>
    {
        var spelled = underscores ? name.Replace('-', '_') : name;
        return casing switch
        {
            0 => spelled,
            1 => spelled.ToUpperInvariant(),
            2 => spelled.ToLowerInvariant(),
            _ => spelled + "-Extra",
        };
    });

    [Fact]
    public void ANameTheValidatorAcceptsNeverOwnsAHeaderTheProxyOwnsItself() =>
        Gen.Select(Names, Gen.Bool).Check(
            20_000,
            t =>
            {
                var name = t.Item2 ? t.Item1 : t.Item1.TrimEnd('*');
                if (ForwardAuthHeaderNames.Problem(name, response: true) is not null)
                {
                    return;
                }

                var provider = ProviderOwning(name);
                foreach (var protectedName in ProtectedNames)
                {
                    foreach (var spelling in new[] { protectedName, protectedName.Replace('-', '_'), protectedName.ToUpperInvariant(), protectedName.ToLowerInvariant().Replace('-', '_') })
                    {
                        Assert.False(provider.Owns(spelling), $"'{name}' is accepted for the provider's answer, yet owns '{spelling}', a header the proxy owns");
                    }
                }
            });

    [Fact]
    public void ANameTheValidatorAcceptsForAProviderOwnsNoFurtherNameThanThatSpellingOrItsPrefix() =>
        Gen.Select(Names, Spellings).Check(
            10_000,
            t =>
            {
                var (name, spelling) = t;
                if (ForwardAuthHeaderNames.Problem(name, response: true) is not null)
                {
                    return;
                }

                Assert.Equal(Model.Owns([name], spelling), ProviderOwning(name).Owns(spelling));
            });

    [Fact]
    public void TheProxyStripsExactlyTheNamesAPlainModelSaysAreOwned() =>
        Gen.Select(Names.List[1, 3], Names.List[0, 6], Spellings.List[0, 4]).Check(
            8_000,
            t =>
            {
                var (owned, clientNames, spellings) = t;
                var valid = owned.Where(n => ForwardAuthHeaderNames.Problem(n, response: true) is null).ToList();
                if (valid.Count == 0)
                {
                    return;
                }

                var provider = Provider([.. valid]);
                var names = clientNames.Concat(spellings).Where(static n => !n.Contains('*', StringComparison.Ordinal)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                using HttpRequestMessage upstream = new();

                // A content header (Content-Length, Content-Encoding) isn't a request header the message takes; the others are.
                names = [.. names.Where(name => upstream.Headers.TryAddWithoutValidation(name, "v"))];

                ForwardedHeadersTransform.ApplyForwardAuth(upstream.Headers, [provider], answered: null);

                var kept = upstream.Headers.Select(static h => h.Key).Order(StringComparer.OrdinalIgnoreCase).ToList();
                var expected = names.Where(n => !Model.Owns(valid, n)).Order(StringComparer.OrdinalIgnoreCase).ToList();
                Assert.Equal(expected, kept, StringComparer.OrdinalIgnoreCase);
            });

    private static CompiledAuthProvider ProviderOwning(string name) => Provider([name]);

    private static CompiledAuthProvider Provider(string[] responseHeaders)
    {
        var spec = Spec("http://127.0.0.1:1/auth", b => b.ResponseHeaders = responseHeaders);
        return new CompiledAuthProvider(spec, new ForwardAuthRuntime().Bind(spec));
    }

    private static class Model
    {
        // An owned name ending in '*' is a prefix; '_' and '-' are one character; case is ignored.
        public static bool Owns(IEnumerable<string> owned, string header)
        {
            var folded = Fold(header);
            foreach (var name in owned)
            {
                var prefix = name.EndsWith('*');
                var stem = Fold(prefix ? name[..^1] : name);
                if (prefix ? folded.StartsWith(stem, StringComparison.Ordinal) : folded == stem)
                {
                    return true;
                }
            }

            return false;
        }

        private static string Fold(string name) => name.Replace('_', '-').ToUpperInvariant();
    }
}
