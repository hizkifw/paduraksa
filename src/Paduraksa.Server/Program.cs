// Paduraksa composition root: one process, three hosts (core, public, admin) sharing one set of process-wide
// singletons, which Hosting/SharedServices builds and owns. See Hosting/PaduraksaHost for the start and stop order.
using Paduraksa.Server.Cli;

return await PaduraksaCli.InvokeAsync(args).ConfigureAwait(false);
