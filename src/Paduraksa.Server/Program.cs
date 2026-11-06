// Paduraksa composition root.
// Two WebApplications share one set of core singletons (snapshot store, metrics, vault):
//   - public:  :80 / :443 (H1/H2/H3) -> WAF gate -> YARP
//   - admin:   :9443 -> dashboard (Blazor Server), /api/v1, /mcp, /metrics
// See docs/PLAN.md §3. Placeholder until M1.
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/", () => "Paduraksa");
app.Run();
