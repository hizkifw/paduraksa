namespace Paduraksa.Proxy.Analytics;

/// <summary>What kind of automated client a user agent names. <see cref="None"/> is anything that isn't one.</summary>
public enum BotClass
{
    None = 0,
    SearchEngine = 1,
    AiCrawler = 2,
    Monitor = 3,
    SocialPreview = 4,
    Scanner = 5,
    Library = 6,
    Unknown = 7,
}

/// <summary>The kind of device a request came from. <see cref="Bot"/> is any automated client, <see cref="Other"/> a television, a console or an agent that says nothing.</summary>
public enum DeviceClass
{
    Desktop = 0,
    Mobile = 1,
    Tablet = 2,
    Bot = 3,
    Other = 4,
}

/// <summary>A user agent as <see cref="UserAgentClassifier"/> read it.</summary>
/// <param name="Browser">The browser family, or <c>Unknown</c> for a request with no user agent and <c>Other</c> for one that isn't a browser the table knows. Empty for a bot.</param>
/// <param name="BrowserVersion">The major version, or 0 when none could be read.</param>
/// <param name="Os">The operating system family, or <c>Unknown</c>/<c>Other</c> likewise.</param>
/// <param name="Device">The kind of device.</param>
/// <param name="Bot">The kind of bot, or <see cref="BotClass.None"/>.</param>
/// <param name="BotName">The bot's name (<c>Googlebot</c>, <c>curl</c>), or null when it isn't one.</param>
public sealed record UserAgentInfo(string Browser, int BrowserVersion, string Os, DeviceClass Device, BotClass Bot, string? BotName)
{
    /// <summary>The family and major version as one label (<c>Chrome 131</c>), or the family alone when the version is unknown.</summary>
    public string BrowserLabel => BrowserVersion > 0 ? $"{Browser} {BrowserVersion}" : Browser;

    public bool IsBot => Bot != BotClass.None;

    /// <summary>What an agent is counted as when the drain was too far behind to classify it (never cached): not a bot, device other, family and system "Unclassified".</summary>
    public static UserAgentInfo Unclassified { get; } = new("Unclassified", 0, "Unclassified", DeviceClass.Other, BotClass.None, null);

    /// <summary>The bot as a table key, its kind and its name (<c>SearchEngine|Googlebot</c>), built once with the agent so counting a bot allocates nothing; null for a client that isn't one.</summary>
    public string? BotKey { get; init; }

    /// <summary>Splits a <see cref="BotKey"/> back into the kind and the name; a key that isn't one reads as an unknown bot of that name.</summary>
    public static (BotClass Kind, string Name) ParseBotKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var bar = key.IndexOf('|', StringComparison.Ordinal);
        return bar > 0 && Enum.TryParse<BotClass>(key.AsSpan(0, bar), out var kind) && kind != BotClass.None ? (kind, key[(bar + 1)..]) : (BotClass.Unknown, key);
    }
}
