namespace Paduraksa.Core.Model;

/// <summary>
/// The statuses an operator can give a page of their own, and the maintenance page. A page is looked up by its key: the
/// status itself, or <see cref="MaintenanceKey"/>. The list is what the gate and the upstream can answer with a built-in page
/// (PLAN §7.3), so every key has one to fall back to.
/// </summary>
public static class ErrorPageStatuses
{
    /// <summary>The key of the maintenance page: not an HTTP status, so it can't collide with one.</summary>
    public const int MaintenanceKey = 0;

    /// <summary>The status a maintenance answer carries unless the host says otherwise.</summary>
    public const int DefaultMaintenanceStatus = 503;

    /// <summary>The longest <c>Retry-After</c> a maintenance answer may send, in seconds (a week).</summary>
    public const int MaxRetryAfterSeconds = 7 * 24 * 60 * 60;

    /// <summary>The statuses with a page of their own, in the order the document and the dashboard list them.</summary>
    public static IReadOnlyList<int> Statuses { get; } = Array.AsReadOnly([403, 404, 413, 414, 421, 429, 431, 502, 503, 504]);

    /// <summary>Every key: the statuses, then <see cref="MaintenanceKey"/>.</summary>
    public static IReadOnlyList<int> Keys { get; } = Array.AsReadOnly([.. Statuses, MaintenanceKey]);

    /// <summary>
    /// What a maintenance answer may carry as its status: 503 is the honest one (and the built-in default); the others are for
    /// a host that must look healthy to a monitor (200) or look gone (403, 404, 410), or fail the way a load balancer expects.
    /// </summary>
    public static IReadOnlyList<int> MaintenanceStatuses { get; } = Array.AsReadOnly([503, 200, 500, 502, 504, 403, 404, 410]);

    /// <summary>The position of <paramref name="key"/> in <see cref="Keys"/>, or -1. Cheap enough for the error path.</summary>
    public static int IndexOf(int key) => key switch
    {
        403 => 0,
        404 => 1,
        413 => 2,
        414 => 3,
        421 => 4,
        429 => 5,
        431 => 6,
        502 => 7,
        503 => 8,
        504 => 9,
        MaintenanceKey => 10,
        _ => -1,
    };

    /// <summary>How the document and the validator's paths spell <paramref name="key"/>: <c>404</c>, or <c>maintenance</c>.</summary>
    public static string NameOf(int key) => key == MaintenanceKey ? "maintenance" : key.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The standard name of a status the pages know, as the built-in headings spell it; empty for any other.</summary>
    public static string StatusText(int status) => status switch
    {
        200 => "OK",
        403 => "Forbidden",
        404 => "Not Found",
        410 => "Gone",
        413 => "Content Too Large",
        414 => "URI Too Long",
        421 => "Misdirected Request",
        429 => "Too Many Requests",
        431 => "Request Header Fields Too Large",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        _ => string.Empty,
    };
}
