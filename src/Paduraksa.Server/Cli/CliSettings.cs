using Paduraksa.Application.Identity;
using Paduraksa.Application.Settings;
using Paduraksa.Core.Options;
using Paduraksa.Data;
using Paduraksa.Server.Configuration;
using Paduraksa.Server.Hosting;

namespace Paduraksa.Server.Cli;

/// <summary>
/// The sign-in policy the command line enforces: the server's own precedence (a value the environment sets explicitly, else the one the config
/// database stores, else the default), so a minimum password length raised in the dashboard binds <c>admin reset-password</c> too.
/// </summary>
internal static class CliSettings
{
    /// <summary>
    /// The identity options the server would run with now. Call after the config database is migrated: the stored settings are the latest revision's,
    /// read from it without writing. A database that stores none or can't be read leaves the environment's values and the defaults, as the server does.
    /// </summary>
    public static async Task ApplyStoredAsync(LiveOptions<IdentityOptions> identity, PaduraksaOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(options);

        ServerSettingsState state = new(options.ServerSettingEnvironment);
        state.Take(await StoredServerSettings.ReadAsync(Path.Combine(options.DataDirectory, ConfigDatabase.FileName), cancellationToken).ConfigureAwait(false));
        identity.Set(ServerSettingsBindings.ResolveIdentity(options.Admin.ToIdentityOptions(), state.Current, options.Admin.TouchInterval));
    }
}
