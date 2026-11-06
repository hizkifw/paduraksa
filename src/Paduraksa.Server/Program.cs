// Paduraksa composition root (PLAN §3).
// One process, three hosts sharing one set of core singletons (Hosting/SharedServices):
//   - core:    non-web host owning process-wide background services; starts first, stops last
//   - public:  :80 / :443 (H1/H2; H3 after spike S1) -> gate -> YARP (placeholder 404 until M1)
//   - admin:   :9443 -> /healthz, /readyz; later dashboard, /api/v1, /mcp, /metrics
using Paduraksa.Server.Cli;

return await PaduraksaCli.InvokeAsync(args).ConfigureAwait(false);
