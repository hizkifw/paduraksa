namespace Paduraksa.Contracts.Commands;

/// <summary>
/// A change notification for the operational resources: a certificate, ACME account, DNS provider or ACME order change was
/// committed and audited. Names what happened, never a value: a page that shows the resource reads it again.
/// </summary>
/// <param name="Action">The audit action (<c>certificate.add</c>, <c>acme.account.create</c>, <c>acme.order.failed</c>, ...).</param>
/// <param name="Target">The typed-ID string of what changed, when the event names one.</param>
public sealed record OperationalChange(string Action, string? Target);
