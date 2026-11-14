using System.Globalization;
using System.Net;
using System.Net.Sockets;

using Paduraksa.Server.Configuration;

namespace Paduraksa.Server.Cli.Doctor;

/// <summary>Each configured port is free, held by the running Paduraksa, or held by something else.</summary>
internal sealed class ListenersCheck : IDoctorCheck
{
    public string Name => "listeners";

    public bool NeedsOptions => true;

    private sealed record Listener(string Id, string Label, IPAddress Address, int Port, bool Udp);

    public async Task<IReadOnlyList<CheckResult>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
    {
        var listeners = Listeners(context.Options!);
        List<CheckResult> results = [];
        foreach (var listener in listeners)
        {
            var state = context.Ports.Probe(listener.Address, listener.Port, listener.Udp);
            results.Add(state switch
            {
                PortState.Free => CheckResult.Ok(listener.Id, $"{Describe(listener)} is free."),
                PortState.InUse => await HeldAsync(listener, context, cancellationToken).ConfigureAwait(false),
                PortState.Denied => await DeniedAsync(listener, context, cancellationToken).ConfigureAwait(false),
                _ => CheckResult.Info(listener.Id, $"{Describe(listener)} could not be tested on this machine."),
            });
        }

        return results;
    }

    private static string Describe(Listener listener) =>
        $"{listener.Label} ({(listener.Udp ? "udp" : "tcp")} {listener.Port.ToString(CultureInfo.InvariantCulture)})";

    private static async Task<CheckResult> HeldAsync(Listener listener, DoctorContext context, CancellationToken cancellationToken)
    {
        var admin = await context.RunningAdminAsync(cancellationToken).ConfigureAwait(false);
        if (admin.IsPaduraksa)
        {
            return CheckResult.Ok(listener.Id, $"{Describe(listener)} is held by the running Paduraksa (its admin answers at {admin.Uri}).");
        }

        var holder = admin.Kind == AdminKind.Other && listener.Port == admin.Uri?.Port ? $" and it answers {admin.Status}, which is not Paduraksa" : string.Empty;
        return CheckResult.Failure(
            listener.Id,
            $"{Describe(listener)} is held by another process{holder}: the server cannot bind it.",
            $"Find the holder (ss -{(listener.Udp ? "ulnp" : "ltnp")} 'sport = :{listener.Port.ToString(CultureInfo.InvariantCulture)}') and stop it, or move Paduraksa to another port ({PortSetting(listener)}). A Paduraksa left over from an earlier run holds it too.");
    }

    // Binding below 1024 as an ordinary user is refused before the kernel says whether the port is taken, so a running server can't be told from a free port.
    private static async Task<CheckResult> DeniedAsync(Listener listener, DoctorContext context, CancellationToken cancellationToken)
    {
        var admin = await context.RunningAdminAsync(cancellationToken).ConfigureAwait(false);
        var message = $"{Describe(listener)} could not be tested as {context.Files.CurrentUser}: binding a port below 1024 needs root or CAP_NET_BIND_SERVICE, so a free port cannot be told from a taken one.";
        return admin.IsPaduraksa
            ? CheckResult.Info(listener.Id, message + " A Paduraksa is running here, so it may be its own.")
            : CheckResult.Warning(listener.Id, message, "Run doctor as the account the server runs as (docker compose exec paduraksa paduraksa doctor), or as root.");
    }

    private static string PortSetting(Listener listener) => listener.Id switch
    {
        "listener-http" => "Paduraksa__Listen__Http",
        "listener-https" or "listener-https-udp" => "Paduraksa__Listen__Https",
        "listener-admin" => "Paduraksa__Listen__Admin",
        _ => "Paduraksa__Listen__AdminLoopbackHttp",
    };

    private static List<Listener> Listeners(PaduraksaOptions options)
    {
        var listen = options.Listen;
        var any = Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any;
        var adminAddress = IPAddress.TryParse(listen.AdminBind, out var bind) ? bind : any;
        List<Listener> listeners =
        [
            new("listener-http", "public HTTP", any, listen.Http, Udp: false),
            new("listener-https", "public HTTPS", any, listen.Https, Udp: false),
        ];

        if (listen.Http3)
        {
            listeners.Add(new("listener-https-udp", "HTTP/3", any, listen.Https, Udp: true));
        }

        listeners.Add(new("listener-admin", "admin", adminAddress, listen.Admin, Udp: false));
        if (listen.AdminLoopbackHttp > 0)
        {
            listeners.Add(new("listener-admin-loopback", "admin over plain HTTP on loopback", IPAddress.Loopback, listen.AdminLoopbackHttp, Udp: false));
        }

        // Port 0 is "any free port" (tests only): there is nothing to hold.
        return [.. listeners.Where(l => l.Port > 0)];
    }
}
