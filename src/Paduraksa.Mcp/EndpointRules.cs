namespace Paduraksa.Mcp;

/// <summary>An endpoint, or every endpoint under a path, as a rule of the request policy.</summary>
/// <param name="Method">An HTTP method, or <c>*</c> for every one.</param>
/// <param name="Path">An API path with <c>{x}</c> for a parameter, as the API description writes it.</param>
/// <param name="Prefix">Also every path below <paramref name="Path"/>.</param>
/// <param name="Reason">Why, in the words the model is told.</param>
/// <param name="InSpec">The endpoint is in the API description today. A rule for one that is not (yet) is there so that it is refused the day it appears.</param>
public sealed record EndpointRule(string Method, string Path, bool Prefix, string Reason, bool InSpec = true)
{
    /// <summary>Whether the rule covers <paramref name="path"/>, a request path or a template; a <c>{x}</c> segment of the rule stands for any one segment.</summary>
    public bool Covers(string method, string path)
    {
        if (Method != "*" && !string.Equals(Method, method, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rule = Path.Split('/');
        var given = path.Split('/');
        if (given.Length < rule.Length || (!Prefix && given.Length != rule.Length))
        {
            return false;
        }

        for (var i = 0; i < rule.Length; i++)
        {
            var isParameter = rule[i].Length > 2 && rule[i][0] == '{' && rule[i][^1] == '}';
            if (!isParameter && !string.Equals(rule[i], given[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// The one list of what a script cannot call through <c>paduraksa.request</c>, whatever the token could do over HTTP (PLAN §11.2), and the list of
/// what looks sensitive and was looked at and allowed. The test <c>McpRequestPolicyApiTests</c> walks the generated API description and fails for an
/// operation whose path matches <see cref="SensitivePath"/> and is on neither list, so a new endpoint that carries a secret, changes who may sign in,
/// or hands out a backup has to be classified when it is added.
/// </summary>
public static class EndpointRules
{
    /// <summary>The paths that must be on one of the lists below (matched case-insensitively against an operation's path).</summary>
    public const string SensitivePath =
        "backup|restore|acme|token|user|role|session|secret|credential|vault|eab|password|totp|recovery|auth|login|setup|/me\\b|key|metrics|openapi|docs"
        + "|dns-provider|acme-account|notification|upload|import|export|certificate|/ca\\b";

    /// <summary>Refused outright: no token, scope or <c>confirm</c> changes it.</summary>
    public static IReadOnlyList<EndpointRule> Refused { get; } =
    [
        new("*", "/api/v1/backup", true, "backups hold the whole configuration and the sealed secrets; creating and restoring them is for the operator, not a script"),
        new("*", "/api/v1/restore", true, "restoring replaces the whole configuration; it is for the operator, not a script", InSpec: false),
        new("*", "/api/v1/tokens", true, "a script that could mint or revoke API tokens could give itself more access"),
        new("*", "/api/v1/users", true, "accounts, roles and credentials are not for a script"),
        new("*", "/api/v1/roles", true, "accounts, roles and credentials are not for a script", InSpec: false),
        new("*", "/api/v1/sessions", true, "sessions are the account owner's"),
        new("*", "/api/v1/me", true, "the account's password, second factor and recovery codes are the account owner's"),
        new("*", "/api/v1/setup", true, "first-run setup is not for a script"),
        new("*", "/api/v1/auth/login", true, "a script acts as the token it was given and never signs in"),
        new("*", "/api/v1/auth/logout", true, "a script acts as the token it was given and never signs in"),
        new("*", "/api/v1/auth/second-factor", true, "a script acts as the token it was given and never signs in"),
        new("POST", "/api/v1/dns-providers", false, "the request would carry a DNS credential, which must never pass through a model"),
        new("PUT", "/api/v1/dns-providers/{id}", false, "the request would carry a DNS credential, which must never pass through a model"),
        new("POST", "/api/v1/acme-accounts", false, "the request would carry an ACME external account binding key, which must never pass through a model"),
        new("POST", "/api/v1/notification-channels", false, "the request would carry a notification secret, which must never pass through a model"),
        new("PUT", "/api/v1/notification-channels/{id}", false, "the request would carry a notification secret, which must never pass through a model"),
        new("POST", "/api/v1/certificates", false, "a certificate upload carries a private key, which must never pass through a model"),
        new("GET", "/api/v1/logs/tail", false, "the live tail is a stream; read the newest entries with GET /api/v1/logs/recent"),
        new("*", "/api/v1/openapi.json", false, "the API description is what search is for; reading it whole defeats that", InSpec: false),
        new("GET", "/api/v1/metrics", false, "that is the Prometheus scrape; read GET /api/v1/metrics/overview"),
        new("*", "/metrics", true, "use GET /api/v1/metrics/overview", InSpec: false),
    ];

    /// <summary>
    /// Read-only requests that are POSTs because they take a body: they change nothing, so they need no <c>confirm</c>. Anything else that is not a
    /// GET is a write.
    /// </summary>
    public static IReadOnlyList<EndpointRule> ReadOnlyPosts { get; } =
    [
        new("POST", "/api/v1/simulate", false, "a simulation changes nothing"),
        new("POST", "/api/v1/drafts/{id}/review", false, "a review is a dry run of the draft and changes nothing"),
        new("POST", "/api/v1/error-pages/preview", false, "a preview renders a page and changes nothing"),
    ];

    /// <summary>
    /// Operations whose path looks sensitive and that were looked at: each says why it is allowed. A write among them still needs <c>confirm</c>, and
    /// every one is still checked by the API's own permission for the token.
    /// </summary>
    public static IReadOnlyList<EndpointRule> Reviewed { get; } =
    [
        new("GET", "/api/v1/auth/me", false, "says who the token is and what it may do; no secret"),
        new("*", "/api/v1/authProviders", true, "forward-auth providers name an endpoint and the headers to relay; a credential in one is a vault reference, never a value"),
        new("GET", "/api/v1/dns-providers", true, "providers are listed without their credentials: no response type carries a secret"),
        new("DELETE", "/api/v1/dns-providers/{id}", false, "removes a provider and its stored credential; carries no secret and needs confirm"),
        new("POST", "/api/v1/dns-providers/{id}/test", false, "tests the stored credential and reports a verdict only; needs confirm"),
        new("GET", "/api/v1/acme-orders", false, "the orders in progress and their state; no key"),
        new("GET", "/api/v1/acme-accounts", true, "accounts are listed without their keys: no response type carries a secret"),
        new("DELETE", "/api/v1/acme-accounts/{id}", false, "removes an account and its stored key; carries no secret and needs confirm"),
        new("GET", "/api/v1/notification-channels", true, "channels are listed without their secrets: no response type carries a secret"),
        new("DELETE", "/api/v1/notification-channels/{id}", false, "removes a channel and its secret; carries no secret and needs confirm"),
        new("POST", "/api/v1/notification-channels/{id}/test", false, "sends one test notification; needs confirm"),
        new("GET", "/api/v1/notifications/history", false, "the events that were published and what happened to each on every channel: our own messages and the senders' fixed error lines, no secret and no channel setting"),
        new("POST", "/api/v1/notifications/deliveries/{id}/retry", false, "sends a failed or expired notification again; the request carries nothing; needs confirm"),
        new("POST", "/api/v1/notifications/events/{id}/retry", false, "sends an event's failed or expired notifications again; the request carries nothing; needs confirm"),
        new("GET", "/api/v1/certificates", true, "the inventory and orders hold no private key"),
        new("POST", "/api/v1/certificates/acme", false, "orders a certificate from the CA; no key is returned; needs confirm"),
        new("POST", "/api/v1/certificates/local-ca", true, "issues from the local CA; no key is returned; needs confirm"),
        new("POST", "/api/v1/certificates/self-signed", false, "makes a self-signed certificate; no key is returned; needs confirm"),
        new("POST", "/api/v1/certificates/{id}/reissue", false, "renews a certificate; no key is returned; needs confirm"),
        new("POST", "/api/v1/certificates/{id}/renew", false, "renews a certificate; no key is returned; needs confirm"),
        new("PUT", "/api/v1/certificates/{id}", false, "renames a certificate; needs confirm"),
        new("DELETE", "/api/v1/certificates/{id}", false, "removes a certificate; needs confirm"),
        new("GET", "/api/v1/ca", true, "the local CA's root certificate is public"),
        new("*", "/api/v1/config", true, "the configuration document, its export and import; every secret in it is a reference, and an import is a write that needs confirm"),
        new("GET", "/api/v1/metrics/overview", false, "the live overview; read-only"),
    ];
}
