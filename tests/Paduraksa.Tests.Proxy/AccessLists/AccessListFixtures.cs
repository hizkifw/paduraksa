using System.Net;
using System.Text;

using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Model;
using Paduraksa.Core.Security;
using Paduraksa.Proxy.AccessLists;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Proxy.AccessLists;

/// <summary>A clock the test moves: one tick is a millisecond.</summary>
internal sealed class StepClock : TimeProvider
{
    private long _ms;

    public override long TimestampFrequency => 1000;

    public override long GetTimestamp() => Interlocked.Read(ref _ms);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ms, (long)by.TotalMilliseconds);
}

/// <summary>Access lists through the real gate: the hashes are made once (PBKDF2 is slow on purpose), and a request is a context with a
/// peer address and an optional Basic credential.</summary>
internal static class AccessListFixtures
{
    public const string Password = "correct horse";

    public static readonly string AliceHash = PasswordHash.Create(Password);

    public static readonly string BobHash = PasswordHash.Create("battery staple");

    public static AccessListSpec List(
        string label,
        string[]? allow = null,
        string[]? deny = null,
        bool drop = false,
        AccessListSatisfy satisfy = AccessListSatisfy.All,
        bool users = true,
        bool passUpstream = false,
        string? realm = null) => new()
        {
            Id = TestIds.AccessList(label),
            Name = label,
            Allow = allow ?? [],
            Deny = deny ?? [],
            DropDenied = drop,
            Satisfy = satisfy,
            PassCredentialsUpstream = passUpstream,
            Realm = realm ?? label,
            Users = users ? [new AccessListUserSpec("alice", AliceHash), new AccessListUserSpec("bob", BobHash)] : [],
        };

    public static ProxyConfigModel Model(AccessListSpec list, string host = "acl.test") => new()
    {
        Upstreams = [TestSnapshots.Upstream("web", "http://127.0.0.1:1")],
        AccessLists = [list],
        Hosts = [new HostSpec
        {
            Id = TestIds.Host(host),
            Names = [host],
            Routes = [TestSnapshots.Route("root", "web") with { Policies = [new AccessListPolicySpec(list.Id)] }],
        }],
    };

    public static GateMiddleware GateFor(AccessListRuntime runtime, ProxyConfigModel model, out RuntimeSnapshot snapshot)
    {
        snapshot = TestSnapshots.Bound(model, options: new SnapshotCompilerOptions { AccessLists = runtime });
        SnapshotStore store = new();
        store.Publish(snapshot);
        return new GateMiddleware(store, new TrustedProxyClientAddressSource());
    }

    public static string Basic(string user, string password) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

    public static DefaultHttpContext Context(string client, string? authorization = null, string host = "acl.test", string path = "/")
    {
        var context = TestContexts.Create(host, path, remote: IPAddress.Parse(client));
        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        return context;
    }

    public static GateDecision Decide(GateMiddleware gate, string client, string? authorization = null, string host = "acl.test") =>
        gate.Decide(Context(client, authorization, host));

    /// <summary>The request through the whole gate, waiting for a verification slot where it must: <c>pass</c> (forwarded),
    /// <c>drop</c>, or the status it was answered with.</summary>
    public static Task<string> RunAsync(GateMiddleware gate, string client, string? authorization = null, string host = "acl.test") =>
        RunAsync(gate, Context(client, authorization, host));

    public static async Task<string> RunAsync(GateMiddleware gate, DefaultHttpContext context)
    {
        var forwarded = false;
        await gate.InvokeAsync(context, _ =>
        {
            forwarded = true;
            return Task.CompletedTask;
        });
        return forwarded ? "pass"
            : context.Features.Get<IGateFeature>()!.State.Aborted ? "drop"
            : context.Response.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
