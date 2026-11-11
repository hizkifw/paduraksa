using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Geo;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Pages;
using Paduraksa.Proxy.Simulation;

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
/// <c>satisfy</c> says. Every verdict (pass, 403, drop, 401) the policy gives at once allocates nothing; a password that
/// isn't in the vault's verification cache costs one PBKDF2, which the request waits for without holding a thread.
/// </summary>
/// <remarks>
/// <para><b>Rules.</b> A denied address is refused whatever it sends. An allow list that has entries lets only those
/// addresses pass; with <c>satisfy: all</c> (and users) the others get 403, with <c>any</c> they are asked for a password
/// instead. Without users only the address applies; without address rules only the password does.</para>
/// <para><b>Credentials.</b> The name is compared in constant time with every user's, so where in the list it matched isn't
/// observable; an unknown name (and a user with no password) still pays one PBKDF2 against a dummy hash, so it is indistinguishable
/// by timing from a wrong password. The hashes and the cache are the vault's (<see cref="IPasswordVerifier"/>): this class hands
/// it the credential and the bytes the client sent and gets a verdict back. A password that isn't in the cache defers the verdict (<see cref="IDeferredPolicy"/>): the request waits in
/// <see cref="AccessListVerificationQueue"/> for one of at most as many concurrent checks as the machine has processors (never
/// fewer than 8; one client runs at most two of them), runs its PBKDF2 and then carries on through the rest of the chain, or is
/// answered 401. A request that can't get a place in the line, or waits too long, is answered 429. A verified credential is remembered in
/// the vault's cache until five minutes after its last use, per client address masked as the rate limit masks it (IPv4 whole, IPv6
/// by /64). The same credentials never run twice at once: a request waits while an identical one is being checked and then finds
/// the result in the cache.</para>
/// <para><b>Allocation.</b> Every synchronous path is 0 B, including the cache miss that defers. The wait and the check run
/// after the decision and allocate a small, bounded amount (a pooled waiter, the continuation); the PBKDF2 itself measured 0 B
/// on .NET 10.0.14 (the distro build and Microsoft's 10.0.403).</para>
/// </remarks>
public sealed class AccessListPolicy : IDeferredPolicy, IPolicyTraceFacts
{
    private static readonly PolicyDecision Busy = PolicyDecision.Respond(
        PrecomputedResponse.Text(StatusCodes.Status429TooManyRequests, "Too many requests. Please slow down.\n", htmlPage: ErrorPages.TooManyRequests), "1");

    private readonly CompiledAccessList _list;
    private readonly AccessListStage _stage;
    private readonly AccessListRuntime _runtime;
    private readonly bool _deferPasswords;

    internal AccessListPolicy(CompiledAccessList list, AccessListStage stage, AccessListRuntime runtime, bool deferPasswordsOnPlainHttp = false)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(runtime);
        _list = list;
        _stage = stage;
        _runtime = runtime;
        _deferPasswords = deferPasswordsOnPlainHttp;
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

    Metrics.GateOutcome IRequestPolicy.Outcome => Metrics.GateOutcome.RefusedAccessList;

    string IRequestPolicy.Explain(in RequestView request, PolicyDecision verdict) =>
        AccessListExplanation.Describe(_list, _stage, request.State.ClientAddress, request.State.ClientCountry, request.State.AccessListOutcome, verdict);

    void IPolicyTraceFacts.AddFacts(in RequestView request, PolicyDecision verdict, List<GateFact> facts)
    {
        var outcome = request.State.AccessListOutcome;
        facts.Add(new("accessList", _list.Name));
        facts.Add(new("address", AccessListExplanation.Word(outcome.Address)));
        if (_list.HasCountryRules)
        {
            facts.Add(new("country", request.State.ClientCountry.ToString()));
            facts.Add(new("countryRule", AccessListExplanation.Word(_list.CountryVerdictOf(request.State.ClientCountry))));
        }

        facts.Add(new("credentials", AccessListExplanation.Word(outcome.Credentials)));
        facts.Add(new("satisfy", _list.Satisfy == AccessListSatisfy.Any ? "any" : "all"));
    }

    /// <summary>Whether the address side alone refuses a request: a denied address or country always, and one outside a non-empty
    /// allow list unless a password can still let it in (<c>satisfy: any</c> with users).</summary>
    internal static bool AddressRefuses(CompiledAccessList list, IpKey client, CountryCode country) =>
        Refuses(list, list.AddressVerdictOf(client, country));

    private static bool Refuses(CompiledAccessList list, AddressVerdict verdict) =>
        verdict == AddressVerdict.Denied
        || (verdict == AddressVerdict.NotInAllow && !(list.HasUsers && list.Satisfy == AccessListSatisfy.Any));

    private AccessListOutcome Run(in RequestView request)
    {
        var list = _list;
        var state = request.State;
        var client = state.ClientAddress;

        // A simulation moves no counter, as it takes no token and touches no cache.
        var stats = request.IsSimulation ? null : _runtime.Stats;
        var ip = list.AddressVerdictOf(client, state.ClientCountry);
        var inAllow = ip == AddressVerdict.Allowed;

        if (Refuses(list, ip))
        {
            stats?.CountAddressRefused();
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
            stats?.CountAllowed();
            return new AccessListOutcome(PolicyDecision.Continue, ip, CredentialVerdict.NotAsked);
        }

        if (_deferPasswords && !state.IsHttps)
        {
            stats?.CountAllowed();
            return new AccessListOutcome(PolicyDecision.Continue, ip, CredentialVerdict.Deferred);
        }

        var credentials = Authenticate(request.Http.Request, client, stats, stats is null ? Phase.Simulation : Phase.Decide);
        return new AccessListOutcome(credentials == CredentialVerdict.Pending ? PolicyDecision.Defer : Conclude(credentials, state, stats), ip, credentials);
    }

    private PolicyDecision Conclude(CredentialVerdict credentials, GateRequestState state, AccessListStats? stats)
    {
        switch (credentials)
        {
            case CredentialVerdict.Verified or CredentialVerdict.VerifiedFromCache:
                stats?.CountAllowed();
                if (!_list.PassCredentialsUpstream)
                {
                    state.StripAuthorization = true;
                }

                return PolicyDecision.Continue;
            case CredentialVerdict.Busy:
                return Busy;
            default:
                stats?.CountUnauthorized();
                return _list.Unauthorized;
        }
    }

    async ValueTask<PolicyDecision> IDeferredPolicy.CompleteAsync(HttpContext http, GateRequestState state)
    {
        var client = state.ClientAddress;
        var stats = _runtime.Stats;
        var key = CredentialKey(http.Request, client);
        var slot = await _runtime.Queue.EnterAsync(client, http.RequestAborted, key).ConfigureAwait(false);
        if (slot == VerificationSlot.Cancelled)
        {
            return PolicyDecision.Drop;
        }

        if (slot != VerificationSlot.Granted)
        {
            return Busy;
        }

        CredentialVerdict credentials;
        try
        {
            credentials = Authenticate(http.Request, client, stats, Phase.Verify);
        }
        finally
        {
            _runtime.Queue.Exit(client, key);
        }

        return Conclude(credentials, state, stats);
    }

    /// <summary>Which part of the check a call is: a simulation (no cache, no slot, no counters), the synchronous decision (only
    /// the cache; a miss is <see cref="CredentialVerdict.Pending"/>), or the check itself, once a slot is held.</summary>
    private enum Phase : byte
    {
        Simulation,
        Decide,
        Verify,
    }

    private CredentialVerdict Authenticate(HttpRequest request, IpKey client, AccessListStats? stats, Phase phase)
    {
        Span<byte> buffer = stackalloc byte[BasicCredentials.MaxDecodedLength + 64];
        if (!TryRead(request, buffer, out var credentials, out var user, out var unreadable))
        {
            return unreadable;
        }

        var colon = credentials.IndexOf((byte)':');
        var name = credentials[..colon];
        var password = credentials[(colon + 1)..];
        Span<byte> masked = stackalloc byte[16];
        client.MaskTo(32, 64).WriteBytes(masked);
        var verifier = _runtime.Verifier;

        // Looked up again in the Verify phase: a request ahead in the line may have verified these very credentials.
        if (user is not null && phase != Phase.Simulation && verifier.TryVerifyCached(user.Credential, name, password, masked).Hit)
        {
            stats!.CountCacheHit();
            return CredentialVerdict.VerifiedFromCache;
        }

        if (phase == Phase.Decide)
        {
            return CredentialVerdict.Pending;
        }

        stats?.CountVerification();
        var verdict = verifier.Verify(user?.Credential, name, password, masked, remember: phase != Phase.Simulation);
        return user is null ? CredentialVerdict.UnknownUser
            : verdict == PasswordVerdict.Verified ? CredentialVerdict.Verified
            : verdict == PasswordVerdict.NoPassword ? CredentialVerdict.NoPassword
            : CredentialVerdict.WrongPassword;
    }

    /// <summary>The credentials of a request's <c>Authorization</c> header and the user they name (null: nobody), decoded into
    /// <paramref name="buffer"/>; false, with the verdict, when there are none or they can't be read.</summary>
    private bool TryRead(
        HttpRequest request, Span<byte> buffer, out ReadOnlySpan<byte> credentials, out CompiledAccessListUser? user, out CredentialVerdict failure)
    {
        credentials = default;
        user = null;
        var header = request.Headers.Authorization;
        failure = header.Count == 0 ? CredentialVerdict.Missing : CredentialVerdict.Unreadable;
        if (header.Count != 1 || BasicCredentials.TryParse(header[0], buffer, out var length) != BasicParse.Ok)
        {
            return false;
        }

        credentials = buffer[..length];
        var name = credentials[..credentials.IndexOf((byte)':')];

        // Every user is compared, with no early exit, so the position of a match isn't something a client can time.
        var users = _list.Users;
        for (var i = 0; i < users.Length; i++)
        {
            if (CryptographicOperations.FixedTimeEquals(name, users[i].Name))
            {
                user = users[i];
            }
        }

        return true;
    }

    // Names the credentials a request is about to have checked, so the line never runs the same ones twice at once; 0 when the
    // name is nobody's or has no password (every such check is its own, against the dummy hash). The vault's name for an attempt
    // is a keyed hash: it says nothing about what was sent.
    private ulong CredentialKey(HttpRequest request, IpKey client)
    {
        Span<byte> buffer = stackalloc byte[BasicCredentials.MaxDecodedLength + 64];
        if (!TryRead(request, buffer, out var credentials, out var user, out _) || user is null)
        {
            return 0;
        }

        var colon = credentials.IndexOf((byte)':');
        Span<byte> masked = stackalloc byte[16];
        client.MaskTo(32, 64).WriteBytes(masked);
        return (ulong)_runtime.Verifier.TryVerifyCached(user.Credential, credentials[..colon], credentials[(colon + 1)..], masked).AttemptKey;
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

    /// <summary>The user exists but the vault holds no password for them (a rollback brought them back after their credential was deleted).</summary>
    NoPassword,
    Verified,
    VerifiedFromCache,
    Busy,

    /// <summary>No slot has been asked for yet: the verdict waits for one (<see cref="IDeferredPolicy"/>).</summary>
    Pending,

    /// <summary>A plain-HTTP request to a host that redirects: the password is asked after the redirect.</summary>
    Deferred,
}

internal readonly record struct AccessListOutcome(PolicyDecision Decision, AddressVerdict Address, CredentialVerdict Credentials);

/// <summary>Several lists' address rules as one host-chain check, for a host whose routes name different lists: a request
/// is refused before route matching only when no route's list could let its address in.</summary>
public sealed class AccessListUnionPolicy : IRequestPolicy, IPolicyTraceFacts
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
        var country = request.State.ClientCountry;
        foreach (var list in _lists)
        {
            if (!AccessListPolicy.AddressRefuses(list, client, country))
            {
                return PolicyDecision.Continue;
            }
        }

        return _refusal;
    }

    string IRequestPolicy.Name => "accessList";

    Metrics.GateOutcome IRequestPolicy.Outcome => Metrics.GateOutcome.RefusedAccessList;

    void IPolicyTraceFacts.AddFacts(in RequestView request, PolicyDecision verdict, List<GateFact> facts)
    {
        facts.Add(new("accessList", string.Join(", ", Array.ConvertAll(_lists, static list => list.Name))));

        // Whether some route's list lets the address through to the route; passwords are the route's to ask.
        facts.Add(new("address", verdict.Action == PolicyAction.Continue ? "allowed" : "refused"));
        facts.Add(new("credentials", "notAsked"));
    }

    string IRequestPolicy.Explain(in RequestView request, PolicyDecision verdict)
    {
        var country = request.State.ClientCountry;
        var who = string.Create(CultureInfo.InvariantCulture, $"client {request.State.ClientAddress}")
            + (Array.Exists(_lists, static list => list.HasCountryRules) ? $" ({Countries.NameOf(country)}, {country})" : string.Empty);
        var names = string.Join(", ", Array.ConvertAll(_lists, static list => $"'{list.Name}'"));
        return verdict.Action == PolicyAction.Continue
            ? $"the host's routes use different access lists ({names}); {who} passes the address rules of at least one, and its route decides"
            : $"the host's routes use different access lists ({names}); {who} is refused by the address rules of every one: {(verdict.Action == PolicyAction.Drop ? "dropped" : "answered " + verdict.Response!.StatusCode)}";
    }
}
