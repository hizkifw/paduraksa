namespace Paduraksa.Dashboard;

/// <summary>The dashboard's content security policy (PLAN §10.1, §12), for its HTML documents only; the API keeps its own.</summary>
public static class DashboardSecurity
{
    /// <summary>
    /// Scripts only from this origin: Blazor's <c>blazor.web.js</c>, MudBlazor's script and the dashboard's own, all served by the app (no
    /// CDN, no inline script, no <c>eval</c>). Styles from this origin plus <c>'unsafe-inline'</c>, which MudBlazor needs: its components
    /// set <c>style</c> attributes and its theme provider writes a <c>&lt;style&gt;</c> element of CSS variables, neither of which a nonce
    /// or hash can cover. <c>connect-src 'self'</c> covers the circuit's WebSocket (same origin), <c>img-src</c> allows <c>data:</c> for
    /// MudBlazor's inline SVG backgrounds, forms post only here, and nothing may frame the page.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; "
        + "script-src 'self'; "
        + "style-src 'self' 'unsafe-inline'; "
        + "img-src 'self' data:; "
        + "font-src 'self'; "
        + "connect-src 'self'; "
        + "manifest-src 'self'; "
        + "base-uri 'self'; "
        + "form-action 'self'; "
        + "frame-ancestors 'none'";
}
