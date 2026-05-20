using System.Net;
using System.Text;
using System.Text.Json;
using System.Globalization;
using NUnit.Framework;

namespace Framework.Reports.Reporters;

[Framework.Reports.ReporterAlias("Allure")]
public sealed class AllureReporter : IReporter
{
    private readonly object _sync = new();
    private readonly Dictionary<string, TestState> _tests = new(StringComparer.OrdinalIgnoreCase);
    private readonly AsyncLocal<string?> _currentTest = new();

    private string _suiteName = "TestSuite";
    private string _environment = "local";
    private string _browser = "chrome";
    private string _resultsDirectory = string.Empty;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    public void InitializeSuite(string suiteName, string environment, string browser)
    {
        _suiteName = string.IsNullOrWhiteSpace(suiteName) ? "TestSuite" : suiteName;
        _environment = string.IsNullOrWhiteSpace(environment) ? "local" : environment;
        _browser = string.IsNullOrWhiteSpace(browser) ? "chrome" : browser;

        var reportsDirectory = ReportManager.GetReportsDirectory();
        _resultsDirectory = reportsDirectory;
        Directory.CreateDirectory(_resultsDirectory);

        WriteEnvironmentProperties();
    }

    public void BeginTest(string testName, string suiteName)
    {
        var key = string.IsNullOrWhiteSpace(testName) ? Guid.NewGuid().ToString("N") : testName;
        var name = string.IsNullOrWhiteSpace(testName) ? "UnnamedTest" : testName;
        var suite = string.IsNullOrWhiteSpace(suiteName) ? _suiteName : suiteName;

        _currentTest.Value = key;

        lock (_sync)
        {
            _tests[key] = new TestState(name, suite, DateTimeOffset.UtcNow);
        }
    }

    public void AddStep(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var key = ResolveCurrentKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (_sync)
        {
            if (_tests.TryGetValue(key, out var state))
            {
                state.Steps.Add($"{DateTime.Now:HH:mm:ss} - {message}");
            }
        }
    }

    public void AttachFile(string name, string filePath, string mimeType)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        var key = ResolveCurrentKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        try
        {
            EnsureResultsDirectory();
            var extension = Path.GetExtension(filePath);
            var safeExtension = string.IsNullOrWhiteSpace(extension) ? ".bin" : extension;
            var source = $"{Guid.NewGuid():N}-attachment{safeExtension}";
            var targetPath = Path.Combine(_resultsDirectory, source);
            File.Copy(filePath, targetPath, overwrite: true);

            lock (_sync)
            {
                if (_tests.TryGetValue(key, out var state))
                {
                    state.Attachments.Add(new AttachmentState(
                        string.IsNullOrWhiteSpace(name) ? "Attachment" : name,
                        source,
                        string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType));
                }
            }
        }
        catch
        {
            // Ignore attachment copy failures to avoid affecting test flow.
        }
    }

    public void AttachContent(string name, string mimeType, string content, string fileExtension)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var key = ResolveCurrentKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        try
        {
            EnsureResultsDirectory();
            var normalizedExtension = NormalizeExtension(fileExtension);
            var source = $"{Guid.NewGuid():N}-attachment{normalizedExtension}";
            var targetPath = Path.Combine(_resultsDirectory, source);
            File.WriteAllText(targetPath, content);

            lock (_sync)
            {
                if (_tests.TryGetValue(key, out var state))
                {
                    state.Attachments.Add(new AttachmentState(
                        string.IsNullOrWhiteSpace(name) ? "Attachment" : name,
                        source,
                        string.IsNullOrWhiteSpace(mimeType) ? "text/plain" : mimeType));
                }
            }
        }
        catch
        {
            // Ignore attachment write failures to avoid affecting test flow.
        }
    }

    public void RecordTestResult(string testName, string outcome, string? errorMessage, TimeSpan duration, string? priority = null, string? testType = null)
    {
        var key = ResolveCurrentKey(testName);
        var defaultStartedAt = DateTimeOffset.UtcNow - duration;

        TestState state;
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(key) && _tests.TryGetValue(key, out var existingState))
            {
                state = existingState;
                _tests.Remove(key);
            }
            else
            {
                var name = string.IsNullOrWhiteSpace(testName) ? "UnnamedTest" : testName;
                state = new TestState(name, _suiteName, defaultStartedAt);
            }
        }

        var startedAt = state.StartedAtUtc;
        var finishedAt = startedAt + duration;
        if (finishedAt < startedAt)
        {
            finishedAt = DateTimeOffset.UtcNow;
        }

        var allureStatus = MapStatus(outcome);
        var statusDetails = string.IsNullOrWhiteSpace(errorMessage)
            ? new { message = (string?)null, trace = (string?)null }
            : new { message = (string?)errorMessage, trace = (string?)errorMessage };

        var steps = state.Steps.Select(step => new
        {
            name = step,
            status = "passed",
            stage = "finished",
            start = startedAt.ToUnixTimeMilliseconds(),
            stop = finishedAt.ToUnixTimeMilliseconds()
        }).ToList();

        var labels = new List<object>
        {
            new { name = "suite", value = state.SuiteName },
            new { name = "framework", value = "nunit" },
            new { name = "language", value = "csharp" },
            new { name = "host", value = Environment.MachineName },
            new { name = "thread", value = Environment.CurrentManagedThreadId.ToString() },
            new { name = "environment", value = _environment },
            new { name = "browser", value = _browser }
        };

        if (!string.IsNullOrWhiteSpace(priority))
        {
            labels.Add(new { name = "priority", value = priority });
        }

        if (!string.IsNullOrWhiteSpace(testType))
        {
            labels.Add(new { name = "testType", value = testType });
        }

        var uuid = Guid.NewGuid().ToString("N");
        var result = new
        {
            uuid,
            name = state.TestName,
            fullName = $"{state.SuiteName}.{state.TestName}",
            status = allureStatus,
            stage = "finished",
            statusDetails,
            start = startedAt.ToUnixTimeMilliseconds(),
            stop = finishedAt.ToUnixTimeMilliseconds(),
            labels,
            steps,
            attachments = state.Attachments.Select(a => new { name = a.Name, source = a.Source, type = a.Type }).ToList()
        };

        try
        {
            EnsureResultsDirectory();
            var json = JsonSerializer.Serialize(result, JsonOptions);
            var resultPath = Path.Combine(_resultsDirectory, $"{uuid}-result.json");
            File.WriteAllText(resultPath, json);
        }
        catch
        {
            // Ignore output failures to avoid affecting test flow.
        }

        _currentTest.Value = null;
    }

    public void FinalizeSuite()
    {
        GenerateHtmlReport();
    }

    private void GenerateHtmlReport()
    {
        try
        {
            var resultFiles = Directory.GetFiles(_resultsDirectory, "*-result.json", SearchOption.TopDirectoryOnly);
            if (resultFiles.Length == 0)
            {
                return;
            }

            var allResults = new List<AllureTestResult>();
            var startedAt = DateTimeOffset.MaxValue;
            var finishedAt = DateTimeOffset.MinValue;

            foreach (var resultFile in resultFiles)
            {
                try
                {
                    var json = File.ReadAllText(resultFile);
                    var result = JsonSerializer.Deserialize<AllureTestResult>(json, JsonOptions);
                    if (result != null)
                    {
                        allResults.Add(result);
                        var resultStarted = DateTimeOffset.FromUnixTimeMilliseconds(result.Start);
                        var resultFinished = DateTimeOffset.FromUnixTimeMilliseconds(result.Stop);
                        if (resultStarted < startedAt)
                            startedAt = resultStarted;
                        if (resultFinished > finishedAt)
                            finishedAt = resultFinished;
                    }
                }
                catch
                {
                    // Ignore individual result file parsing failures
                }
            }

            if (allResults.Count == 0)
            {
                return;
            }

            if (startedAt == DateTimeOffset.MaxValue)
                startedAt = DateTimeOffset.Now;
            if (finishedAt == DateTimeOffset.MinValue)
                finishedAt = DateTimeOffset.Now;

            var html = BuildHtmlFromResults(allResults, startedAt, finishedAt);
            var reportPath = Path.Combine(_resultsDirectory, "execution-report.html");
            File.WriteAllText(reportPath, html);
        }
        catch
        {
            // Ignore HTML generation failures to avoid affecting test execution
        }
    }

    private string BuildHtmlFromResults(List<AllureTestResult> results, DateTimeOffset startedAt, DateTimeOffset finishedAt)
    {
        var total = results.Count;
        var passed = results.Count(r => r.Status == "passed");
        var failed = results.Count(r => r.Status == "failed");
        var skipped = results.Count(r => r.Status == "skipped");
        var broken = results.Count(r => r.Status == "broken");

        var highPriority = results.Count(r => r.Labels?.Any(l => l.Name == "priority" && l.Value == "High") ?? false);
        var mediumPriority = results.Count(r => r.Labels?.Any(l => l.Name == "priority" && l.Value == "Medium") ?? false);
        var lowPriority = results.Count(r => r.Labels?.Any(l => l.Name == "priority" && l.Value == "Low") ?? false);
        var unspecifiedPriority = total - highPriority - mediumPriority - lowPriority;

        var apiCount = results.Count(r => r.Labels?.Any(l => l.Name == "testType" && l.Value == "API") ?? false);
        var uiCount = results.Count(r => r.Labels?.Any(l => l.Name == "testType" && l.Value == "UI") ?? false);
        var hybridCount = results.Count(r => r.Labels?.Any(l => l.Name == "testType" && l.Value == "Hybrid") ?? false);
        var otherCount = total - apiCount - uiCount - hybridCount;

        var duration = finishedAt - startedAt;

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang='en'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>");
        sb.AppendLine("<title>Allure Execution Report</title>");
        sb.AppendLine($"<style>body{{font-family:Segoe UI,Arial,sans-serif;background:#f5f7fb;color:#1f2937;margin:0}}.page{{max-width:1200px;margin:0 auto;padding:24px}}.card{{background:#fff;border:1px solid #dbe3ef;border-radius:12px;padding:16px;margin-bottom:16px}}{ReportBranding.HeaderCss}.grid{{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:12px}}.metric{{font-size:28px;font-weight:700}}.muted{{color:#6b7280}}.passed{{color:#166534}}.failed{{color:#b91c1c}}.skipped{{color:#9a6700}}.broken{{color:#7c2d12}}.priority-grid{{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:12px}}.priority{{font-weight:700}}.priority-high{{color:#b91c1c}}.priority-medium{{color:#9a6700}}.priority-low{{color:#166534}}.priority-unspecified{{color:#6b7280}}.type-grid{{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:12px}}.type-api{{color:#0f766e}}.type-ui{{color:#1d4ed8}}.type-hybrid{{color:#7c3aed}}.type-other{{color:#6b7280}}.type-pill{{display:inline-block;padding:2px 8px;border-radius:999px;font-size:12px;font-weight:600}}.type-pill-api{{background:#ccfbf1;color:#115e59}}.type-pill-ui{{background:#dbeafe;color:#1e40af}}.type-pill-hybrid{{background:#ede9fe;color:#5b21b6}}.type-pill-other{{background:#e5e7eb;color:#374151}}.stack{{height:10px;border-radius:999px;overflow:hidden;background:#e5e7eb;display:flex}}.stack-api{{background:#14b8a6}}.stack-ui{{background:#3b82f6}}.stack-hybrid{{background:#8b5cf6}}.stack-other{{background:#9ca3af}}.chart-grid{{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px}}.pie-wrap{{position:relative;width:170px;height:170px;margin:8px auto 12px}}.pie-chart{{width:170px;height:170px;border-radius:50%;border:1px solid #e5e7eb}}.pie-center{{position:absolute;top:50%;left:50%;transform:translate(-50%,-50%);width:92px;height:92px;border-radius:50%;background:#fff;border:1px solid #e5e7eb;display:flex;align-items:center;justify-content:center;font-weight:700;color:#111827}}.pie-legend{{list-style:none;margin:0;padding:0;display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:8px}}.pie-legend li{{display:flex;align-items:center;gap:6px;font-size:13px}}.legend-swatch{{width:10px;height:10px;border-radius:50%;display:inline-block}}table{{width:100%;border-collapse:collapse}}th,td{{padding:10px;border-bottom:1px solid #e5e7eb;text-align:left;vertical-align:top}}th{{font-size:12px;text-transform:uppercase;color:#6b7280}}details{{margin:0}}ol{{margin:8px 0 0;padding-left:18px}}.attachments{{margin-top:8px}}.attachment-item{{margin-top:4px}}.attachment-pill{{display:inline-block;padding:2px 8px;border-radius:999px;background:#eef2ff;color:#4338ca;font-size:12px;font-weight:600}}.attachment-inline{{color:#6b7280;font-size:12px;margin-left:4px}}@media (max-width:900px){{.grid,.priority-grid,.type-grid,.chart-grid{{grid-template-columns:repeat(2,minmax(0,1fr))}}}}@media (max-width:640px){{.grid,.priority-grid,.type-grid,.chart-grid{{grid-template-columns:1fr}}.pie-legend{{grid-template-columns:1fr}}}}</style></head><body>");
        sb.AppendLine("<div class='page'>");
        sb.AppendLine(ReportBranding.BuildHeaderCardHtml(
            "Allure Execution Report",
            _suiteName,
            _environment,
            _browser,
            startedAt,
            finishedAt,
            duration));
        sb.AppendLine("<div class='grid'>");
        sb.AppendLine($"<div class='card'><div class='muted'>Total</div><div class='metric'>{total}</div></div>");
        sb.AppendLine($"<div class='card'><div class='muted'>Passed</div><div class='metric passed'>{passed}</div></div>");
        sb.AppendLine($"<div class='card'><div class='muted'>Failed</div><div class='metric failed'>{failed}</div></div>");
        sb.AppendLine($"<div class='card'><div class='muted'>Skipped</div><div class='metric skipped'>{skipped}</div></div>");
        sb.AppendLine("</div>");
        sb.AppendLine("<div class='chart-grid'>");
        sb.AppendLine(BuildPieChartMarkup(
            "Result Distribution",
            new List<PieSlice>
            {
                new("Passed", passed, "#16a34a"),
                new("Failed", failed, "#dc2626"),
                new("Skipped", skipped, "#ca8a04"),
                new("Broken", broken, "#9a3412")
            },
            total));
        sb.AppendLine(BuildPieChartMarkup(
            "Test Type Distribution",
            new List<PieSlice>
            {
                new("API", apiCount, "#14b8a6"),
                new("UI", uiCount, "#3b82f6"),
                new("Hybrid", hybridCount, "#8b5cf6"),
                new("Other", otherCount, "#9ca3af")
            },
            total));
        sb.AppendLine("</div>");
        sb.AppendLine("<div class='card'>");
        sb.AppendLine("<h2 style='margin-top:0'>Suite Distribution</h2>");
        sb.AppendLine("<div class='type-grid'>");
        sb.AppendLine($"<div class='card'><div class='muted'>API Tests</div><div class='metric type-api'>{apiCount}</div></div>");
        sb.AppendLine($"<div class='card'><div class='muted'>UI Tests</div><div class='metric type-ui'>{uiCount}</div></div>");
        sb.AppendLine($"<div class='card'><div class='muted'>Hybrid Tests</div><div class='metric type-hybrid'>{hybridCount}</div></div>");
        sb.AppendLine($"<div class='card'><div class='muted'>Other Tests</div><div class='metric type-other'>{otherCount}</div></div>");
        sb.AppendLine("</div>");
        if (total > 0)
        {
            var apiPercent = Math.Round((double)apiCount * 100 / total, 2);
            var uiPercent = Math.Round((double)uiCount * 100 / total, 2);
            var hybridPercent = Math.Round((double)hybridCount * 100 / total, 2);
            var otherPercent = Math.Max(0, 100 - apiPercent - uiPercent - hybridPercent);
            sb.AppendLine("<div class='stack' style='margin-top:8px'>");
            sb.AppendLine($"<div class='stack-api' style='width:{apiPercent}%'></div>");
            sb.AppendLine($"<div class='stack-ui' style='width:{uiPercent}%'></div>");
            sb.AppendLine($"<div class='stack-hybrid' style='width:{hybridPercent}%'></div>");
            sb.AppendLine($"<div class='stack-other' style='width:{otherPercent}%'></div>");
            sb.AppendLine("</div>");
            sb.AppendLine($"<div class='muted' style='margin-top:6px'>API {apiPercent}% | UI {uiPercent}% | Hybrid {hybridPercent}% | Other {otherPercent}%</div>");
        }
        sb.AppendLine("</div>");
        sb.AppendLine("<div class='priority-grid'>");
        sb.AppendLine($"<div class='card'><div class='muted'>High Priority</div><div class='metric priority priority-high'>{highPriority}</div></div>");
        sb.AppendLine($"<div class='card'><div class='muted'>Medium Priority</div><div class='metric priority priority-medium'>{mediumPriority}</div></div>");
        sb.AppendLine($"<div class='card'><div class='muted'>Low Priority</div><div class='metric priority priority-low'>{lowPriority}</div></div>");
        sb.AppendLine($"<div class='card'><div class='muted'>Unspecified Priority</div><div class='metric priority priority-unspecified'>{unspecifiedPriority}</div></div>");
        sb.AppendLine("</div>");
        sb.AppendLine("<div class='card'><h2 style='margin-top:0'>Tests</h2><table><thead><tr><th>Test</th><th>Type</th><th>Priority</th><th>Outcome</th><th>Duration</th><th>Error</th><th>Steps</th></tr></thead><tbody>");

        foreach (var result in results.OrderBy(r => r.Name))
        {
            var status = result.Status ?? "unknown";
            var statusClass = status switch
            {
                "passed" => "passed",
                "failed" => "failed",
                "skipped" => "skipped",
                "broken" => "broken",
                _ => "muted"
            };

            var priority = result.Labels?.FirstOrDefault(l => l.Name == "priority")?.Value ?? "Unspecified";
            var testType = result.Labels?.FirstOrDefault(l => l.Name == "testType")?.Value ?? "Other";
            var typeBadgeClass = testType switch
            {
                "API" => "type-pill type-pill-api",
                "UI" => "type-pill type-pill-ui",
                "Hybrid" => "type-pill type-pill-hybrid",
                _ => "type-pill type-pill-other"
            };
            var testDuration = result.Stop > 0 && result.Start > 0
                ? TimeSpan.FromMilliseconds(result.Stop - result.Start).ToString()
                : "N/A";

            var stepCount = result.Steps?.Count ?? 0;
            var stepMarkup = stepCount == 0
                ? "<span class='muted'>No steps</span>"
                : $"<details><summary>{stepCount} step(s)</summary><ol>{string.Join(string.Empty, (result.Steps ?? []).Select(s => $"<li>{WebUtility.HtmlEncode(s.Name ?? "")}</li>"))}</ol></details>";

            var attachmentMarkup = BuildAttachmentMarkup(result.Attachments);
            if (!string.IsNullOrWhiteSpace(attachmentMarkup) && !attachmentMarkup.Contains("None", StringComparison.OrdinalIgnoreCase))
            {
                stepMarkup += $"<div class='attachments'><span class='attachment-pill'>Attachments</span>{attachmentMarkup}</div>";
            }
            var errorMarkup = BuildErrorMarkup(result.StatusDetails?.Message, result.StatusDetails?.Trace);

            sb.AppendLine("<tr>");
            sb.AppendLine($"<td>{WebUtility.HtmlEncode(result.Name ?? "")}</td>");
            sb.AppendLine($"<td><span class='{typeBadgeClass}'>{WebUtility.HtmlEncode(testType)}</span></td>");
            sb.AppendLine($"<td>{WebUtility.HtmlEncode(priority)}</td>");
            sb.AppendLine($"<td><span class='{statusClass}'>{WebUtility.HtmlEncode(status)}</span></td>");
            sb.AppendLine($"<td>{testDuration}</td>");
            sb.AppendLine($"<td>{errorMarkup}</td>");
            sb.AppendLine($"<td>{stepMarkup}</td>");
            sb.AppendLine("</tr>");
        }

        sb.AppendLine("</tbody></table></div></div></body></html>");
        return sb.ToString();
    }

    private static string BuildErrorMarkup(string? message, string? trace)
    {
        if (string.IsNullOrWhiteSpace(message) && string.IsNullOrWhiteSpace(trace))
        {
            return "<span class='muted'>N/A</span>";
        }

        if (!string.IsNullOrWhiteSpace(message) && (string.IsNullOrWhiteSpace(trace) || string.Equals(message, trace, StringComparison.Ordinal)))
        {
            return WebUtility.HtmlEncode(message);
        }

        var safeMessage = WebUtility.HtmlEncode(message ?? "Error details");
        var safeTrace = WebUtility.HtmlEncode(trace ?? string.Empty);
        return $"<details><summary>{safeMessage}</summary><div style='margin-top:6px;white-space:pre-wrap;word-break:break-word'>{safeTrace}</div></details>";
    }

    private static string BuildAttachmentMarkup(List<Attachment>? attachments)
    {
        if (attachments is null || attachments.Count == 0)
        {
            return "<span class='muted'>None</span>";
        }

        var items = attachments.Select(attachment =>
        {
            var name = WebUtility.HtmlEncode(attachment.Name ?? "Attachment");
            var source = WebUtility.HtmlEncode(attachment.Source ?? string.Empty);
            var type = WebUtility.HtmlEncode(attachment.Type ?? "application/octet-stream");

            if (string.IsNullOrWhiteSpace(source))
            {
                return $"<li>{name} ({type})</li>";
            }

            return $"<li><a href='{source}' target='_blank' rel='noopener noreferrer'>{name}</a> ({type})</li>";
        });

        return $"<details><summary>{attachments.Count} attachment(s)</summary><ol>{string.Join(string.Empty, items)}</ol></details>";
    }

    private static string BuildPieChartMarkup(string title, List<PieSlice> slices, int total)
    {
        var validSlices = slices.Where(s => s.Value > 0).ToList();
        if (total <= 0 || validSlices.Count == 0)
        {
            return $"<div class='card'><h2 style='margin-top:0'>{WebUtility.HtmlEncode(title)}</h2><div class='muted'>No data available.</div></div>";
        }

        var gradientStops = new List<string>();
        var legendItems = new List<string>();
        var currentPercent = 0d;

        foreach (var slice in validSlices)
        {
            var percent = (double)slice.Value * 100d / total;
            var start = currentPercent;
            var end = Math.Min(100d, currentPercent + percent);
            var startText = start.ToString("0.##", CultureInfo.InvariantCulture);
            var endText = end.ToString("0.##", CultureInfo.InvariantCulture);
            gradientStops.Add($"{slice.Color} {startText}% {endText}%");

            var percentText = percent.ToString("0.##", CultureInfo.InvariantCulture);
            legendItems.Add($"<li><span class='legend-swatch' style='background:{slice.Color}'></span>{WebUtility.HtmlEncode(slice.Label)}: {slice.Value} ({percentText}%)</li>");
            currentPercent = end;
        }

        var gradient = string.Join(", ", gradientStops);
        return $"<div class='card'><h2 style='margin-top:0'>{WebUtility.HtmlEncode(title)}</h2><div class='pie-wrap'><div class='pie-chart' style='background:conic-gradient({gradient})'></div><div class='pie-center'>{total}</div></div><ul class='pie-legend'>{string.Join(string.Empty, legendItems)}</ul></div>";
    }

    private static string MapStatus(string? outcome)
    {
        if (string.IsNullOrWhiteSpace(outcome))
        {
            return "unknown";
        }

        return outcome.Trim().ToLowerInvariant() switch
        {
            "passed" => "passed",
            "failed" => "failed",
            "skipped" => "skipped",
            "inconclusive" => "broken",
            _ => "unknown"
        };
    }

    private string? ResolveCurrentKey(string? fallback = null)
    {
        if (!string.IsNullOrWhiteSpace(_currentTest.Value))
        {
            return _currentTest.Value;
        }

        try
        {
            var nunitTestName = TestContext.CurrentContext?.Test?.Name;
            if (!string.IsNullOrWhiteSpace(nunitTestName))
            {
                return nunitTestName;
            }
        }
        catch
        {
            // Ignore context read failures and fall back to supplied key.
        }

        return fallback;
    }

    private static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return ".txt";
        }

        return extension.StartsWith('.') ? extension : $".{extension}";
    }

    private void EnsureResultsDirectory()
    {
        if (string.IsNullOrWhiteSpace(_resultsDirectory))
        {
            _resultsDirectory = ReportManager.GetReportsDirectory();
        }

        Directory.CreateDirectory(_resultsDirectory);
    }

    private void WriteEnvironmentProperties()
    {
        try
        {
            EnsureResultsDirectory();
            var lines = new List<string>
            {
                $"environment={_environment}",
                $"browser={_browser}",
                $"suite={_suiteName}"
            };

            var environmentPath = Path.Combine(_resultsDirectory, "environment.properties");
            File.WriteAllLines(environmentPath, lines);
        }
        catch
        {
            // Ignore metadata write failures to avoid affecting test flow.
        }
    }

    private sealed class AllureTestResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string? Name { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("status")]
        public string? Status { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("start")]
        public long Start { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("stop")]
        public long Stop { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("labels")]
        public List<Label>? Labels { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("steps")]
        public List<Step>? Steps { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("statusDetails")]
        public StatusDetails? StatusDetails { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("attachments")]
        public List<Attachment>? Attachments { get; set; }
    }

    private sealed class Label
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string? Name { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("value")]
        public string? Value { get; set; }
    }

    private sealed class Step
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string? Name { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("status")]
        public string? Status { get; set; }
    }

    private sealed class StatusDetails
    {
        [System.Text.Json.Serialization.JsonPropertyName("message")]
        public string? Message { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("trace")]
        public string? Trace { get; set; }
    }

    private sealed class Attachment
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string? Name { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("source")]
        public string? Source { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("type")]
        public string? Type { get; set; }
    }

    private sealed class TestState
    {
        public TestState(string testName, string suiteName, DateTimeOffset startedAtUtc)
        {
            TestName = testName;
            SuiteName = suiteName;
            StartedAtUtc = startedAtUtc;
        }

        public string TestName { get; }
        public string SuiteName { get; }
        public DateTimeOffset StartedAtUtc { get; }
        public List<string> Steps { get; } = [];
        public List<AttachmentState> Attachments { get; } = [];
    }

    private sealed record AttachmentState(string Name, string Source, string Type);

    private sealed record PieSlice(string Label, int Value, string Color);
}
