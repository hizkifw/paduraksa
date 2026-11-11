using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Config;

/// <summary>A page to preview: the page as written and the status it would be served with.</summary>
/// <param name="Page">The page, as it would be stored.</param>
/// <param name="Status">The status it is served with, 100 to 599; its <c>{status}</c> and <c>{statusText}</c> say it. For a page of one of the
/// statuses that is the status itself, for the maintenance page the status the host answers maintenance with.</param>
/// <param name="Maintenance">The page is a maintenance page, which a host may serve with another status than an error page's.</param>
public sealed record ErrorPagePreviewRequest(ErrorPageContent Page, int Status = 503, bool Maintenance = false);

/// <summary>A page rendered the way the proxy serves it, with sample values (<c>{requestId}</c>, <c>{host}</c>, <c>{cfRay}</c>).</summary>
/// <param name="Html">The page's document, which a client shows in a sandboxed frame without scripts.</param>
/// <param name="ContentSecurityPolicy">The <c>Content-Security-Policy</c> the proxy serves it with.</param>
/// <param name="SampleRequest">The ID <c>{requestId}</c> was filled with.</param>
/// <param name="SampleHost">The name <c>{host}</c> was filled with.</param>
/// <param name="SampleCfRay">The ray <c>{cfRay}</c> was filled with.</param>
public sealed record ErrorPagePreview(string Html, string ContentSecurityPolicy, string SampleRequest, string SampleHost, string SampleCfRay);
