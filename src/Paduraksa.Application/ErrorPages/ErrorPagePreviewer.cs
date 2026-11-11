using System.Text;

using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Pages;

namespace Paduraksa.Application.ErrorPages;

/// <summary>
/// Renders an error page for the dashboard and the API: judged by the validator's own rules, then compiled and written by the proxy's
/// (<see cref="ErrorPagePreviews"/>), so a preview is the bytes a visitor would get. Pure and stateless: it reads no config and changes none.
/// </summary>
public static class ErrorPagePreviewer
{
    /// <summary>The preview, or the validator's errors for a page that can't be served as written (their paths point into the request).</summary>
    public static (ErrorPagePreview? Preview, IReadOnlyList<ConfigError> Errors) Render(ErrorPagePreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<ConfigError> errors = [];
        if (request.Page is not { } page)
        {
            return (null, [new ConfigError("/page", ConfigErrorCodes.ValueNull, "the page can't be null")]);
        }

        if (request.Status is < 100 or > 599)
        {
            return (null, [new ConfigError("/status", ConfigErrorCodes.EnumUndefined, $"the status must be 100-599, got {request.Status}")]);
        }

        if (request.Maintenance && !ErrorPageStatuses.IsMaintenanceStatus(request.Status))
        {
            return (null, [new ConfigError("/status", ConfigErrorCodes.MaintenanceStatusInvalid, $"a maintenance answer's status is one of {string.Join(", ", ErrorPageStatuses.MaintenanceStatuses)}, got {request.Status}")]);
        }

        // The same rules as a stored page: a document of one entry, so nothing here can disagree with what saving would say.
        var key = request.Maintenance ? ErrorPageStatuses.MaintenanceKey : request.Status;
        if (ErrorPageStatuses.IndexOf(key) < 0)
        {
            return (null, [new ConfigError("/status", ConfigErrorCodes.EnumUndefined, $"{request.Status} isn't a status with an error page: {string.Join(", ", ErrorPageStatuses.Statuses)}, or a maintenance page")]);
        }

        ConfigDocument document = new(Settings: new GlobalSettings(Defaults: new HostSettings { ErrorPages = new ErrorPagesSettings().WithEntry(key, page) }));
        var prefix = $"/settings/defaults/errorPages/{ErrorPageStatuses.NameOf(key)}";
        foreach (var error in ConfigValidator.Validate(document))
        {
            errors.Add(error with { Path = error.Path.StartsWith(prefix, StringComparison.Ordinal) ? "/page" + error.Path[prefix.Length..] : error.Path });
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        var rendered = ErrorPagePreviews.Render(page.Mode, page.Heading, page.Text, page.Html, request.Status);
        return (
            new ErrorPagePreview(
                Encoding.UTF8.GetString(rendered.Body),
                rendered.ContentSecurityPolicy,
                ErrorPagePreviews.SampleRequestId,
                ErrorPagePreviews.SampleHost,
                ErrorPagePreviews.SampleRay),
            []);
    }
}
