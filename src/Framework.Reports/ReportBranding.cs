using System.Net;

namespace Framework.Reports;

internal static class ReportBranding
{
    // Official celsior logo embedded once for all reporters.
    internal const string LogoDataUri = "data:image/png;base64,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";

    internal const string HeaderCss = ".report-header{display:flex;align-items:flex-start;gap:14px}.report-logo{width:270px;height:auto;max-height:94px;object-fit:contain;display:block}@media (max-width:640px){.report-header{display:block}.report-logo{margin-bottom:10px;width:200px}}";

    internal static string BuildHeaderCardHtml(
        string title,
        string suite,
        string environment,
        string browser,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        TimeSpan duration)
    {
        return $"<div class='card'><div class='report-header'><img class=\"report-logo\" src=\"{WebUtility.HtmlEncode(LogoDataUri)}\" alt=\"Company logo\" /><div><h1 style='margin:0 0 8px'>{WebUtility.HtmlEncode(title)}</h1><div class='muted'>Suite: {WebUtility.HtmlEncode(suite)} | Environment: {WebUtility.HtmlEncode(environment)} | Browser: {WebUtility.HtmlEncode(browser)}</div><div class='muted'>Started: {startedAt:yyyy-MM-dd HH:mm:ss zzz} | Ended: {endedAt:yyyy-MM-dd HH:mm:ss zzz} | Duration: {duration}</div></div></div></div>";
    }
}