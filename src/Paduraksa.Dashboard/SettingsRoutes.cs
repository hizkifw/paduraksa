namespace Paduraksa.Dashboard;

/// <summary>The settings pages: the instance-wide defaults and who may use the instance.</summary>
public static class SettingsRoutes
{
    /// <summary>The global defaults: the bottom layer of host settings, unknown names, the default certificate, trusted proxies.</summary>
    public const string Defaults = "/settings/defaults";

    /// <summary>The users and their roles.</summary>
    public const string Users = "/settings/users";

    /// <summary>The notification channels: where an upstream going down, a certificate about to expire and the like are sent.</summary>
    public const string Notifications = "/settings/notifications";

    /// <summary>The full backup: a passphrase-encrypted download of the config and the vault (<c>backup:create</c>, the Owner's).</summary>
    public const string Backup = "/settings/backup";

    /// <summary>The API tokens: one's own, and everyone's with <c>tokens:all</c>.</summary>
    public const string Tokens = "/settings/tokens";
}
