using Paduraksa.Contracts.Metrics;
using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Analytics;

/// <summary>
/// A label and how many times it was seen, for a table or a distribution. <paramref name="Error"/> is how far <paramref name="Count"/> may be
/// above the truth when the table comes from a sketch (top paths, referers, user agents, largest responses): the true count is at least
/// <c>Count - Error</c> and at most <c>Count</c>. It is 0 for a count that is exact.
/// </summary>
/// <param name="Label">What was counted: a path, a referer's scheme and host (or <c>direct</c>, <c>(same site)</c>), a user agent, a browser, a status code.</param>
/// <param name="Count">How many requests (or, for the largest responses, how many bytes).</param>
/// <param name="Error">The most <paramref name="Count"/> may overshoot by; 0 when exact.</param>
public sealed record AnalyticsCount(string Label, long Count, long Error);

/// <summary>One path in a table of paths.</summary>
/// <param name="Path">The decoded path without its query string, at most 256 characters.</param>
/// <param name="Requests">Requests to it, an upper bound that may overshoot by <paramref name="Error"/>.</param>
/// <param name="Error">The most <paramref name="Requests"/> may overshoot by.</param>
/// <param name="BytesOut">Response body bytes since the path was first tracked in a sketch; a lower bound on its total.</param>
/// <param name="P95Ms">The 95th-percentile total time in milliseconds, over the requests seen since the path was first tracked.</param>
/// <param name="ErrorRate">The share of those requests answered with a 5xx, 0 to 1.</param>
public sealed record AnalyticsPath(string Path, long Requests, long Error, long BytesOut, double P95Ms, double ErrorRate);

/// <summary>A kind or name of bot with how many requests it made.</summary>
/// <param name="Kind">The kind: <c>searchEngine</c>, <c>aiCrawler</c>, <c>monitor</c>, <c>socialPreview</c>, <c>scanner</c>, <c>library</c> or <c>unknown</c>.</param>
/// <param name="Name">The bot's name (<c>Googlebot</c>, <c>curl</c>), or empty for a row that counts a whole kind.</param>
/// <param name="Count">Requests.</param>
public sealed record AnalyticsBot(string Kind, string Name, long Count);

/// <summary>
/// One step of the charts: what happened over the <paramref name="WidthSeconds"/> from <paramref name="Start"/>. A step whose
/// <paramref name="CoveredSeconds"/> is 0 has no data: Paduraksa was not running (every figure is 0), a gap and not a quiet step. A step that was
/// covered and saw no requests has requests 0.
/// </summary>
/// <param name="Start">The step's first second.</param>
/// <param name="WidthSeconds">How long the step is.</param>
/// <param name="CoveredSeconds">How many of those seconds the server was running to count them.</param>
/// <param name="Requests">Requests in the step.</param>
/// <param name="BytesOut">Response body bytes written to clients in the step.</param>
/// <param name="BytesIn">Request body bytes read from clients in the step.</param>
/// <param name="Visitors">Estimated unique visitors in the step (HyperLogLog, about 1.6 % standard error).</param>
/// <param name="ServerErrors">5xx answers in the step.</param>
public sealed record AnalyticsStep(DateTimeOffset Start, int WidthSeconds, int CoveredSeconds, long Requests, long BytesOut, long BytesIn, long Visitors, long ServerErrors);

/// <summary>The headline figures of the range.</summary>
/// <param name="Requests">Requests answered, the connections the gate dropped included.</param>
/// <param name="BytesOut">Response body bytes written to clients, as they went out (an upstream's compression counts compressed). Stretches stored
/// before bodies were counted hold the declared <c>Content-Length</c>s instead.</param>
/// <param name="BytesIn">Request body bytes read from clients; none for stretches stored before they were counted.</param>
/// <param name="Visitors">Estimated unique visitors over the whole range, the union of the steps' and not their sum (about 1.6 % standard error). A visitor active across a restart counts once on each side of it.</param>
/// <param name="ServerErrors">Requests answered with a 5xx.</param>
/// <param name="ErrorRate"><paramref name="ServerErrors"/> over <paramref name="Requests"/>, 0 to 1.</param>
/// <param name="BotRequests">Requests whose user agent is an automated client.</param>
/// <param name="CoveredSeconds">How many seconds of the range the server was running for.</param>
public sealed record AnalyticsTotals(long Requests, long BytesOut, long BytesIn, long Visitors, long ServerErrors, double ErrorRate, long BotRequests, long CoveredSeconds);

/// <summary>
/// The traffic analytics of a range, for all hosts or one: visitors and content, and clients and bots (PLAN §7.4). Computed from the
/// access-log drain, so a host with its access log off is absent. Visitors, the top tables and the per-path figures are approximate by design;
/// each table row carries its own bound.
/// </summary>
/// <param name="At">When it was read.</param>
/// <param name="Range">How far back the figures reach.</param>
/// <param name="StepSeconds">The usual width of one <paramref name="Series"/> step: 5 minutes for the hour, 1 hour for the day, 3 hours for the week and 1 day for the month.</param>
/// <param name="Totals">The headline figures.</param>
/// <param name="Series">The charts, oldest first, from the first step the server observed to the last: every step between them is present, and one the server was not running for has <see cref="AnalyticsStep.CoveredSeconds"/> 0.</param>
/// <param name="StatusClasses">Requests by status class: <c>none</c> (no response), <c>1xx</c> to <c>5xx</c>. Exact.</param>
/// <param name="Statuses">The most common exact status codes. Exact up to 64 distinct codes a bucket.</param>
/// <param name="Methods">Requests by method. Exact up to 16 distinct methods a bucket.</param>
/// <param name="Protocols">Requests by HTTP version: <c>HTTP/1.0</c>, <c>HTTP/1.1</c>, <c>HTTP/2</c>, <c>HTTP/3</c>, <c>other</c>. Exact. (The TLS version is not available to the analytics without work on the request path.)</param>
/// <param name="TopPaths">The most requested paths.</param>
/// <param name="TopReferers">Where requests came from: <c>direct</c>, <c>(same site)</c> and referers reduced to scheme and host.</param>
/// <param name="TopUserAgents">The most common raw user agents.</param>
/// <param name="SlowestPaths">Paths with the highest 95th-percentile time among the tracked ones with at least five requests.</param>
/// <param name="LargestResponses">Paths by the bytes of their declared responses (the count is bytes).</param>
/// <param name="Browsers">Browser families of the clients that are not bots.</param>
/// <param name="BrowserVersions">Browser families with their major version, for the clients that are not bots.</param>
/// <param name="OperatingSystems">Operating system families of the clients that are not bots.</param>
/// <param name="Devices">Requests by device: <c>desktop</c>, <c>mobile</c>, <c>tablet</c>, <c>bot</c>, <c>other</c>. Exact.</param>
/// <param name="BotKinds">Bot requests by kind. Exact.</param>
/// <param name="TopBots">The most active bots by name.</param>
/// <param name="BotsIgnoringRobots">Bots that fetched a path the host's <c>robots.txt</c> disallows for them, by name and kind. Only hosts that serve a managed <c>robots.txt</c> count; monitors and generic libraries are not expected to read it and are left out.</param>
/// <param name="Host">The host the figures are for, or <see langword="null"/> for all of them (and for the requests no host owns, which have no ID: see <paramref name="Unmatched"/>).</param>
/// <param name="Unmatched">True when the figures are for the requests no host owns (scanners and typos).</param>
public sealed record AnalyticsOverview(
    DateTimeOffset At,
    MetricsRange Range,
    int StepSeconds,
    AnalyticsTotals Totals,
    IReadOnlyList<AnalyticsStep> Series,
    IReadOnlyList<AnalyticsCount> StatusClasses,
    IReadOnlyList<AnalyticsCount> Statuses,
    IReadOnlyList<AnalyticsCount> Methods,
    IReadOnlyList<AnalyticsCount> Protocols,
    IReadOnlyList<AnalyticsPath> TopPaths,
    IReadOnlyList<AnalyticsCount> TopReferers,
    IReadOnlyList<AnalyticsCount> TopUserAgents,
    IReadOnlyList<AnalyticsPath> SlowestPaths,
    IReadOnlyList<AnalyticsCount> LargestResponses,
    IReadOnlyList<AnalyticsCount> Browsers,
    IReadOnlyList<AnalyticsCount> BrowserVersions,
    IReadOnlyList<AnalyticsCount> OperatingSystems,
    IReadOnlyList<AnalyticsCount> Devices,
    IReadOnlyList<AnalyticsBot> BotKinds,
    IReadOnlyList<AnalyticsBot> TopBots,
    IReadOnlyList<AnalyticsBot> BotsIgnoringRobots,
    HostId? Host = null,
    bool Unmatched = false);
