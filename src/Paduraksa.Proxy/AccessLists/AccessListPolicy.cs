using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Model;
using Paduraksa.Core.Security;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Pages;

namespace Paduraksa.Proxy.AccessLists;

/// <summary>Which part of an access list the policy applies.</summary>
public enum AccessListStage : byte
{
    /// <summary>The address rules, then the passwords: the whole list.</summary>
    Full,

    /// <summary>Only what the address alone can refuse, for the host chain, which runs before the HTTPS redirect and
    /// before a route is known. Passwords are asked for in the route chain, after the redirect, so they don't travel over
    /// plain HTTP.</summary>
    AddressOnly,
}

/// <summary>
/// One access list in a host's or route's chain: IP allow and deny, then HTTP basic auth, combined as the list's
/// <c>satisfy</c> says. Every verdict (pass, 403, drop, 401) allocates nothing; a password that isn't in the
/// verification cache costs one PBKDF2.
/// </summary>
/// <remarks>
/// <para><b>Rules.</b> A denied address is refused whatever it sends. An allow list that has entries lets only those
/// addresses pass; with <c>satisfy: all</c> (and users) the others get 403, with <c>any</c> they are asked for a password
/// instead. Without users only the address applies; without address rules only the password does.</para>
/// <para><b>Credentials.</b> The name is compared in constant time with every user's, so where in the list it matched isn't
/// observable; an unknown name still pays one PBKDF2 against a dummy hash, so it is indistinguishable by timing from a wrong
/// password. At most as many checks as the machine has processors run at once; the rest are answered 429 rather than
/// queued behind a flood of wrong passwords. A verified credential is remembered for five minutes in
/// <see cref="AccessListVerificationCache"/>, keyed by a keyed hash of the list's user (which changes with the password),
/// the password and the client address masked as the rate limit masks it (IPv4 whole, IPv6 by /64).</para>
/// <para><b>Allocation.</b> Every path is 0 B, a PBKDF2 included on .NET 10.0.14 (the distro build and Microsoft's 10.0.403
/// both measured 0 B); <c>AccessListPolicyTests</c> only bounds a miss at 512 B, in case a runtime's provider allocates its
/// handles.</para>
/// </remarks>
public sealed class AccessListPolicy : IRequestPolicy
{
    private static readonly PolicyDecision Busy = PolicyDecision.Respond(
        PrecomputedResponse.Text(StatusCodes.Status429TooManyRequests, "Too many requests. Please slow down.\n", htmlPage: ErrorPages.TooManyRequests), "1");

    // Checked when a name matches no user, so that a guess at a name costs what a guess at a password does.
    private static readonly ParsedPasswordHash DummyHash = ParseDummy();

    private readonly CompiledAccessList _list;
    private readonly AccessListStage _stage;
    private readonly AccessListRuntime _runtime;

    internal AccessListPolicy(CompiledAccessList list, AccessListStage stage, AccessListRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(runtime);
        _list = list;
        _stage = stage;
        _runtime = runtime;
    }

    public CompiledAccessList List => _list;

    public AccessListStage Stage => _stage;

    public PolicyDecision Evaluate(in RequestView request)
    {
        var outcome = Run(in request);
        if (request.IsSimulation)
        {
            request.State.AccessListOutcome = outcome;
        }

        return outcome.Decision;
    }

    string IRequestPolicy.Name => "accessList";

    string IRequestPolicy.Explain(in RequestView request, PolicyDecision verdict) =>
        AccessListExplanation.Describe(_list, _stage, request.State.ClientAddress, request.State.AccessListOutcome, verdict);

    /// <summary>Whether the address rules alone refuse <paramref name="client"/>: a denied address always, and one outside
    /// a non-empty allow list unless a password can still let it in (<c>satisfy: any</c> with users).</summary>
    internal static bool AddressRefuses(CompiledAccessList list, IpKey client)
    {
        if (!list.Deny.IsEmpty && list.Deny.Contains(client))
        {
            return true;
        }

        if (list.Allow.IsEmpty || (list.HasUsers && list.Satisfy == AccessListSatisfy.Any))
        {
            return false;
        }

        return !list.Allow.Contains(client);
    }

    private AccessListOutcome Run(in RequestView request)
    {
        var list = _list;
        var state = request.State;
        var client = state.ClientAddress;
        var denied = !list.Deny.IsEmpty && list.Deny.Contains(client);
        var restrictsAddress = !list.Allow.IsEmpty;
        var inAllow = restrictsAddress && list.Allow.Contains(client);
        var ip = denied ? AddressVerdict.Denied : !restrictsAddress ? AddressVerdict.Unrestricted : inAllow ? AddressVerdict.Allowed : AddressVerdict.NotInAllow;

        if (AddressRefuses(list, client))
        {
            _runtime.Stats.CountAddressRefused();
            return new AccessListOutcome(list.OnAddressRefused, ip, CredentialVerdict.NotAsked);
        }

        // Past the address rules. Nothing more to ask in the host chain, or of a list without users, or of an address that
        // the list lets in without a password.
        if (_stage == AccessListStage.AddressOnly)
        {
            // The route's own stage counts the request, once: counting here too would double it.
            return new AccessListOutcome(PolicyDecision.Continue, ip, CredentialVerdict.NotAsked);
        }

        if (!list.HasUsers || (inAllow && list.Satisfy == AccessListSatisfy.Any))
        {
            _runtime.Stats.CountAllowed();
            return new AccessListOutcome(PolicyDecision.Continue, ip, CredentialVerdict.NotAsked);
        }

        var credentials = Authenticate(request.Http.Request, client, request.IsSimulation);
        switch (credentials)
        {
            case CredentialVerdict.Verified or CredentialVerdict.VerifiedFromCache:
                _runtime.Stats.CountAllowed();
                if (!list.PassCredentialsUpstream)
                {
                    state.StripAuthorization = true;
                }

                return new AccessListOutcome(PolicyDecision.Continue, ip, credentials);
            case CredentialVerdict.Busy:
                _runtime.Stats.CountBusy();
                return new AccessListOutcome(Busy, ip, credentials);
            default:
                _runtime.Stats.CountUnauthorized();
                return new AccessListOutcome(list.Unauthorized, ip, credentials);
        }
    }

    private CredentialVerdict Authenticate(HttpRequest request, IpKey client, bool simulate)
    {
        var header = request.Headers.Authorization;
        if (header.Count == 0)
        {
            return CredentialVerdict.Missing;
        }

        if (header.Count > 1)
        {
            return CredentialVerdict.Unreadable;
        }

        Span<byte> buffer = stackalloc byte[BasicCredentials.MaxDecodedLength + 64];
        if (BasicCredentials.TryParse(header[0], buffer, out var length) != BasicParse.Ok)
        {
            return CredentialVerdict.Unreadable;
        }

        var credentials = buffer[..length];
        var colon = credentials.IndexOf((byte)':');
        var name = credentials[..colon];
        var password = credentials[(colon + 1)..];

        // Every user is compared, with no early exit, so the position of a match isn't something a client can time.
        CompiledAccessListUser? user = null;
        var users = _list.Users;
        for (var i = 0; i < users.Length; i++)
        {
            if (CryptographicOperations.FixedTimeEquals(name, users[i].Name))
            {
                user = users[i];
            }
        }

        // A simulation neither reads nor writes the cache: it says what a verification would, and it leaves the gate's
        // state exactly as it found it.
        ulong fingerprint = 0;
        if (user is not null && !simulate)
        {
            fingerprint = Fingerprint(user, client, credentials);
            if (_runtime.Cache.Contains(fingerprint, _runtime.NowMs))
            {
                _runtime.Stats.CountCacheHit();
                return CredentialVerdict.VerifiedFromCache;
            }
        }

        if (!simulate && !_runtime.TryEnterVerification(client))
        {
            return CredentialVerdict.Busy;
        }

        bool matches;
        try
        {
            _runtime.Stats.CountVerification();
            matches = PasswordHash.Verify(password, user?.Hash ?? DummyHash);
        }
        finally
        {
            if (!simulate)
            {
                _runtime.ExitVerification(client);
            }
        }

        if (user is null)
        {
            return CredentialVerdict.UnknownUser;
        }

        if (!matches)
        {
            return CredentialVerdict.WrongPassword;
        }

        if (!simulate)
        {
            _runtime.Cache.Add(fingerprint, _runtime.NowMs);
        }

        return CredentialVerdict.Verified;
    }

    // SHA-256 over the process key, the user's fingerprint, the masked client and the credentials as sent. The key is the
    // keyed part: without it nobody can compute, aim or guess an entry. Never leaves the stack.
    private ulong Fingerprint(CompiledAccessListUser user, IpKey client, ReadOnlySpan<byte> credentials)
    {
        Span<byte> input = stackalloc byte[32 + 16 + 16 + BasicCredentials.MaxDecodedLength + 8];
        _runtime.Key.CopyTo(input);
        user.Fingerprint.CopyTo(input[32..]);
        client.MaskTo(32, 64).WriteBytes(input[48..]);
        credentials.CopyTo(input[64..]);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input[..(64 + credentials.Length)], digest);
        CryptographicOperations.ZeroMemory(input);
        var key = BinaryPrimitives.ReadUInt64LittleEndian(digest);
        return key == 0 ? 1 : key;
    }

    private static ParsedPasswordHash ParseDummy()
    {
        var made = PasswordHash.TryParse(PasswordHash.Create(Convert.ToHexString(RandomNumberGenerator.GetBytes(8))), out var parsed);
        return made ? parsed! : throw new InvalidOperationException("The dummy password hash didn't parse.");
    }
}

/// <summary>The address part of a verdict, for the trace.</summary>
internal enum AddressVerdict : byte
{
    Unrestricted,
    Denied,
    Allowed,
    NotInAllow,
}

/// <summary>The password part of a verdict, for the trace.</summary>
internal enum CredentialVerdict : byte
{
    /// <summary>The list didn't get as far as asking (the address decided, or there are no users).</summary>
    NotAsked,
    Missing,
    Unreadable,
    UnknownUser,
    WrongPassword,
    Verified,
    VerifiedFromCache,
    Busy,
}

internal readonly record struct AccessListOutcome(PolicyDecision Decision, AddressVerdict Address, CredentialVerdict Credentials);

/// <summary>Several lists' address rules as one host-chain check, for a host whose routes name different lists: a request
/// is refused before route matching only when no route's list could let its address in.</summary>
public sealed class AccessListUnionPolicy : IRequestPolicy
{
    private readonly CompiledAccessList[] _lists;
    private readonly PolicyDecision _refusal;

    internal AccessListUnionPolicy(CompiledAccessList[] lists)
    {
        ArgumentNullException.ThrowIfNull(lists);
        if (lists.Length == 0)
        {
            throw new ArgumentException("A union needs at least one list.", nameof(lists));
        }

        _lists = lists;

        // Dropped only when every list would drop: a list that answers 403 is the one a client may be told about.
        _refusal = PolicyDecision.Drop;
        foreach (var list in lists)
        {
            if (!list.DropDenied)
            {
                _refusal = list.Forbidden;
                break;
            }
        }
    }

    public PolicyDecision Evaluate(in RequestView request)
    {
        var client = request.State.ClientAddress;
        foreach (var list in _lists)
        {
            if (!AccessListPolicy.AddressRefuses(list, client))
            {
                return PolicyDecision.Continue;
            }
        }

        return _refusal;
    }

    string IRequestPolicy.Name => "accessList";

    string IRequestPolicy.Explain(in RequestView request, PolicyDecision verdict)
    {
        var client = request.State.ClientAddress;
        var names = string.Join(", ", Array.ConvertAll(_lists, static list => $"'{list.Name}'"));
        return verdict.Action == PolicyAction.Continue
            ? string.Create(CultureInfo.InvariantCulture, $"the host's routes use different access lists ({names}); client {client} passes the address rules of at least one, and its route decides")
            : string.Create(CultureInfo.InvariantCulture, $"the host's routes use different access lists ({names}); client {client} is refused by the address rules of every one: {(verdict.Action == PolicyAction.Drop ? "dropped" : "answered " + verdict.Response!.StatusCode)}");
    }
}
