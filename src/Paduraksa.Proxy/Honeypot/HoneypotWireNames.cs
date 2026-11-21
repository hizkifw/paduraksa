namespace Paduraksa.Proxy.Honeypot;

/// <summary>The names the API, the simulator and the design give the honeypot's enums, defined once so a name can't differ between them.</summary>
public static class HoneypotWireNames
{
    /// <summary>The link kind's name: the enum's in camel case, except where the design spells it otherwise (<c>nofollow</c> is the HTML attribute, <c>noindexPage</c> one word).</summary>
    public static string Link(HoneypotLinkKind kind) => kind switch
    {
        HoneypotLinkKind.NoFollow => "nofollow",
        HoneypotLinkKind.NoIndexPage => "noindexPage",
        _ => CamelCase(kind.ToString()),
    };

    public static string CamelCase(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}
