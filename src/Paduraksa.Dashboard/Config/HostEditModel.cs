using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.SettingsArea;

namespace Paduraksa.Dashboard.Config;

/// <summary>
/// The host form's state (PLAN §5.1): what a person edits, turned into a <see cref="HostConfig"/> for the command. It starts from the host
/// as stored (its <see cref="Version"/> is the one the form opened with, so Apply conflicts when someone changed it since) and keeps what
/// the form doesn't show (a route's own settings), so saving never drops a field set elsewhere. A new host gets fresh typed IDs.
/// </summary>
public sealed class HostEditModel
{
    private readonly HostConfig? _original;

    private HostEditModel(HostConfig? original, HostId id)
    {
        _original = original;
        Id = id;
    }

    /// <summary>The host's ID: the stored one, or a new one for a host that doesn't exist yet.</summary>
    public HostId Id { get; }

    /// <summary>Whether the form creates the host (version 0).</summary>
    public bool IsNew => _original is null;

    /// <summary>The version the form was opened with (0 for a new host).</summary>
    public long Version => _original?.Version ?? 0;

    public List<TextEntry> Names { get; } = [];

    public HostKind Kind { get; set; } = HostKind.Proxy;

    public bool Enabled { get; set; } = true;

    public ProfileId? ProfileId { get; set; }

    public CertificateBindingMode CertificateMode { get; set; } = CertificateBindingMode.Auto;

    public CertificateId? CertificateId { get; set; }

    /// <summary>The host's own layer of settings (PLAN §5.3): each field left null inherits from the profile or the global defaults.</summary>
    public HostSettingsModel Settings { get; private init; } = new();

    public List<RouteEntry> Routes { get; } = [];

    public string RedirectTarget { get; set; } = "https://";

    public int RedirectStatus { get; set; } = 301;

    public bool RedirectPreservePath { get; set; } = true;

    public int RespondStatus { get; set; } = 200;

    public string RespondBody { get; set; } = string.Empty;

    public string RespondContentType { get; set; } = RespondConfig.DefaultContentType;

    /// <summary>Tags, separated by commas or spaces.</summary>
    public string Tags { get; set; } = string.Empty;

    public string? Notes { get; set; }

    /// <summary>A form for a new proxy host, with one empty name and one route to <paramref name="upstream"/> (when there is one).</summary>
    public static HostEditModel New(UpstreamId? upstream)
    {
        HostEditModel model = new(null, HostId.New());
        model.Names.Add(new TextEntry());
        model.Routes.Add(new RouteEntry(RouteId.New()) { UpstreamId = upstream });
        return model;
    }

    /// <summary>A form for <paramref name="host"/> as stored.</summary>
    public static HostEditModel From(HostConfig host)
    {
        ArgumentNullException.ThrowIfNull(host);
        HostEditModel model = new(host, host.Id)
        {
            Kind = host.Kind,
            Enabled = host.Enabled,
            ProfileId = host.ProfileId,
            CertificateMode = host.Certificate.Mode,
            CertificateId = host.Certificate.CertificateId,
            Settings = HostSettingsModel.From(host.Settings),
            Tags = string.Join(", ", host.Tags),
            Notes = host.Notes,
        };
        model.Names.AddRange(host.Names.Select(name => new TextEntry { Value = name }));
        if (model.Names.Count == 0)
        {
            model.Names.Add(new TextEntry());
        }

        model.Routes.AddRange(host.Routes.Select(route => new RouteEntry(route.Id, route.Settings)
        {
            Path = route.Path,
            Match = route.Match,
            UpstreamId = route.UpstreamId,
            Order = route.Order,
        }));
        if (host.Redirect is { } redirect)
        {
            model.RedirectTarget = redirect.Target;
            model.RedirectStatus = redirect.StatusCode;
            model.RedirectPreservePath = redirect.PreservePath;
        }

        if (host.Respond is { } respond)
        {
            model.RespondStatus = respond.StatusCode;
            model.RespondBody = respond.Body;
            model.RespondContentType = respond.ContentType;
        }

        return model;
    }

    /// <summary>Adds an empty name field.</summary>
    public void AddName() => Names.Add(new TextEntry());

    /// <summary>Adds a route for <paramref name="upstream"/>, ordered after the others.</summary>
    public void AddRoute(UpstreamId? upstream) =>
        Routes.Add(new RouteEntry(RouteId.New()) { UpstreamId = upstream, Order = Routes.Count == 0 ? 0 : Routes.Max(r => r.Order), Path = "/" });

    /// <summary>The host as the form describes it: what the command sends. Only the kind's own part is included.</summary>
    public HostConfig ToConfig()
    {
        HostSettings settings = Settings.ApplyTo(_original?.Settings ?? new HostSettings());
        if (Kind == HostKind.Admin)
        {
            // The admin host is always HTTPS: the form doesn't offer the redirect, and keeps what was stored.
            settings = settings with { ForceHttps = _original?.Settings.ForceHttps };
        }

        return new HostConfig(
            Id,
            [.. Names.Select(n => n.Value.Trim()).Where(n => n.Length > 0)],
            Version,
            Kind,
            Enabled,
            ProfileId,
            CertificateMode == CertificateBindingMode.Auto && CertificateId is null
                ? new CertificateBinding()
                : new CertificateBinding { Mode = CertificateMode, CertificateId = CertificateMode == CertificateBindingMode.Specific ? CertificateId : null },
            settings,
            Kind == HostKind.Proxy ? [.. Routes.Select(r => r.ToConfig())] : null,
            Kind == HostKind.Redirect ? new RedirectConfig(RedirectTarget.Trim(), RedirectStatus, RedirectPreservePath) : null,
            Kind == HostKind.Respond ? new RespondConfig(RespondStatus, RespondBody, string.IsNullOrWhiteSpace(RespondContentType) ? RespondConfig.DefaultContentType : RespondContentType.Trim()) : null,
            SplitList(Tags),
            string.IsNullOrWhiteSpace(Notes) ? null : Notes);
    }

    /// <summary>A list typed into one box: split on new lines, commas and spaces, empty entries dropped.</summary>
    public static IReadOnlyList<string> SplitList(string? text) =>
        (text ?? string.Empty).Split(['\n', '\r', ',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>One line of a list field (a host name): a class, so the form can bind to it and remove it by reference.</summary>
public sealed class TextEntry
{
    public string Value { get; set; } = string.Empty;
}

/// <summary>A route in the host form; its own settings (the route-level allow list) are kept as they were.</summary>
public sealed class RouteEntry(RouteId id, RouteSettings? settings = null)
{
    public RouteId Id { get; } = id;

    public string Path { get; set; } = "/";

    public RouteMatchKind Match { get; set; } = RouteMatchKind.Prefix;

    public UpstreamId? UpstreamId { get; set; }

    public int Order { get; set; }

    /// <summary>The route as the command sends it. A route without an upstream is sent with an empty one, which validation reports.</summary>
    public RouteConfig ToConfig() =>
        new(Id, UpstreamId ?? default, Order, Match, string.IsNullOrWhiteSpace(Path) ? "/" : Path.Trim(), settings);
}
