using System.Net;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace Framework.Reports.Reporters;

[Framework.Reports.ReporterAlias("Html")]
public sealed class HtmlReporter : IReporter
{
    private readonly object _sync = new();
    private readonly Dictionary<string, List<string>> _stepsByTest = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TestRecord> _records = [];
    private readonly AsyncLocal<string?> _currentTest = new();

    private DateTimeOffset _suiteStartedAt;
    private string _suiteName = "TestSuite";
    private string _environment = "local";
    private string _browser = "chrome";

    private const string AggregateFileName = ".execution-report-aggregate.json";
    private const string UnifiedReportFileName = "execution-report.html";
    private static readonly TimeSpan AggregateStaleAfter = TimeSpan.FromMinutes(20);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public void InitializeSuite(string suiteName, string environment, string browser)
    {
        _suiteStartedAt = DateTimeOffset.Now;
        _suiteName = string.IsNullOrWhiteSpace(suiteName) ? "TestSuite" : suiteName;
        _environment = string.IsNullOrWhiteSpace(environment) ? "local" : environment;
        _browser = string.IsNullOrWhiteSpace(browser) ? "chrome" : browser;

        var reportsDirectory = ReportManager.GetReportsDirectory();
        WithGlobalLock(reportsDirectory, () =>
        {
            var aggregatePath = Path.Combine(reportsDirectory, AggregateFileName);
            var aggregate = LoadAggregate(aggregatePath);

            // Treat as fresh if aggregate is null or stale
            var shouldStartFresh = aggregate is null
                || DateTimeOffset.UtcNow - aggregate.LastUpdatedUtc > AggregateStaleAfter;

            if (shouldStartFresh)
            {
                aggregate = new AggregateStore(
                    Guid.NewGuid().ToString("N"),
                    _suiteStartedAt,
                    DateTimeOffset.UtcNow,
                    _suiteName,
                    _environment,
                    _browser,
                    []);
            }
            else
            {
                var existingAggregate = aggregate!;
                aggregate = existingAggregate with
                {
                    LastUpdatedUtc = DateTimeOffset.UtcNow,
                    SuiteName = existingAggregate.SuiteName,
                    Environment = existingAggregate.Environment,
                    Browser = string.Equals(existingAggregate.Browser, _browser, StringComparison.OrdinalIgnoreCase)
                        ? existingAggregate.Browser
                        : "multiple"
                };
            }

            SaveAggregate(aggregatePath, aggregate);
        });
    }

    public void BeginTest(string testName, string suiteName)
    {
        var key = string.IsNullOrWhiteSpace(testName) ? Guid.NewGuid().ToString("N") : testName;
        _currentTest.Value = key;

        lock (_sync)
        {
            _stepsByTest[key] = [];
        }
    }

    public void AddStep(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var key = ResolveCurrentTestKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (_sync)
        {
            if (!_stepsByTest.TryGetValue(key, out var steps))
            {
                steps = [];
                _stepsByTest[key] = steps;
            }

            steps.Add($"{DateTime.Now:HH:mm:ss} - {message}");
        }
    }

    public void AttachFile(string name, string filePath, string mimeType)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        AddStep($"[Attachment] {name} ({mimeType}) - {filePath}");
    }

    public void AttachContent(string name, string mimeType, string content, string fileExtension)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var preview = content.Length <= 200 ? content : content[..200] + "...";
        AddStep($"[Content] {name} ({mimeType}/{fileExtension}) - {preview}");
    }

    public void RecordTestResult(string testName, string outcome, string? errorMessage, TimeSpan duration, string? priority = null, string? testType = null)
    {
        var key = ResolveCurrentTestKey(testName);

        List<string> steps = [];
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(key) && _stepsByTest.TryGetValue(key, out var existingSteps))
            {
                steps = [.. existingSteps];
                _stepsByTest.Remove(key);
            }

            _records.Add(new TestRecord(testName, outcome, errorMessage, duration, NormalizePriority(priority), NormalizeTestType(testType), steps));
        }

        _currentTest.Value = null;
    }

    private string? ResolveCurrentTestKey(string? fallbackKey = null)
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

        return fallbackKey;
    }

    public void FinalizeSuite()
    {
        try
        {
            List<TestRecord> localRecords;
            lock (_sync)
            {
                localRecords = [.. _records];
            }

            var reportsDirectory = ReportManager.GetReportsDirectory();
            WithGlobalLock(reportsDirectory, () =>
            {
                var aggregatePath = Path.Combine(reportsDirectory, AggregateFileName);
                var aggregate = LoadAggregate(aggregatePath)
                    ?? new AggregateStore(
                        Guid.NewGuid().ToString("N"),
                        _suiteStartedAt,
                        DateTimeOffset.UtcNow,
                        _suiteName,
                        _environment,
                        _browser,
                        []);

                var mergedRecords = new List<TestRecordAggregate>(aggregate.Records.Count + localRecords.Count);
                mergedRecords.AddRange(aggregate.Records);
                mergedRecords.AddRange(localRecords.Select(r => new TestRecordAggregate(r.Name, r.Outcome, r.ErrorMessage, r.Duration, r.Priority, r.TestType, r.Steps)));

                var updatedAggregate = aggregate with
                {
                    LastUpdatedUtc = DateTimeOffset.UtcNow,
                    Browser = string.Equals(aggregate.Browser, _browser, StringComparison.OrdinalIgnoreCase)
                        ? aggregate.Browser
                        : "multiple",
                    Records = mergedRecords
                };

                SaveAggregate(aggregatePath, updatedAggregate);

                var unifiedReportPath = Path.Combine(reportsDirectory, UnifiedReportFileName);
                File.WriteAllText(unifiedReportPath, BuildHtml(updatedAggregate));
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HtmlReporter] Failed to write report: {ex.Message}");
        }
    }

    private string BuildHtml(AggregateStore aggregate)
    {
        var completedAt = DateTimeOffset.Now;
        var records = aggregate.Records;

        var total = records.Count;
        var passed = records.Count(r => string.Equals(r.Outcome, "Passed", StringComparison.OrdinalIgnoreCase));
        var failed = records.Count(r => string.Equals(r.Outcome, "Failed", StringComparison.OrdinalIgnoreCase));
        var skipped = records.Count(r => string.Equals(r.Outcome, "Skipped", StringComparison.OrdinalIgnoreCase));
        var highPriority = records.Count(r => string.Equals(NormalizePriority(r.Priority), "High", StringComparison.OrdinalIgnoreCase));
        var mediumPriority = records.Count(r => string.Equals(NormalizePriority(r.Priority), "Medium", StringComparison.OrdinalIgnoreCase));
        var lowPriority = records.Count(r => string.Equals(NormalizePriority(r.Priority), "Low", StringComparison.OrdinalIgnoreCase));
        var unspecifiedPriority = records.Count(r => string.Equals(NormalizePriority(r.Priority), "Unspecified", StringComparison.OrdinalIgnoreCase));
        var apiCount = records.Count(r => string.Equals(NormalizeTestType(r.TestType), "API", StringComparison.OrdinalIgnoreCase));
        var uiCount = records.Count(r => string.Equals(NormalizeTestType(r.TestType), "UI", StringComparison.OrdinalIgnoreCase));
        var hybridCount = records.Count(r => string.Equals(NormalizeTestType(r.TestType), "Hybrid", StringComparison.OrdinalIgnoreCase));
        var otherTypeCount = records.Count(r => string.Equals(NormalizeTestType(r.TestType), "Other", StringComparison.OrdinalIgnoreCase));
        var duration = completedAt - aggregate.StartedAt;

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang='en'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>");
        sb.AppendLine("<title>Execution Report</title>");
        sb.AppendLine($"<style>body{{font-family:Segoe UI,Arial,sans-serif;background:#f5f7fb;color:#1f2937;margin:0}}.page{{max-width:1200px;margin:0 auto;padding:24px}}.card{{background:#fff;border:1px solid #dbe3ef;border-radius:12px;padding:16px;margin-bottom:16px}}{ReportBranding.HeaderCss}.grid{{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:12px}}.metric{{font-size:28px;font-weight:700}}.muted{{color:#6b7280}}.passed{{color:#166534}}.failed{{color:#b91c1c}}.skipped{{color:#9a6700}}.priority-grid{{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:12px}}.priority{{font-weight:700}}.priority-high{{color:#b91c1c}}.priority-medium{{color:#9a6700}}.priority-low{{color:#166534}}.priority-unspecified{{color:#6b7280}}.type-grid{{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:12px}}.type-api{{color:#0f766e}}.type-ui{{color:#1d4ed8}}.type-hybrid{{color:#7c3aed}}.type-other{{color:#6b7280}}.type-pill{{display:inline-block;padding:2px 8px;border-radius:999px;font-size:12px;font-weight:600}}.type-pill-api{{background:#ccfbf1;color:#115e59}}.type-pill-ui{{background:#dbeafe;color:#1e40af}}.type-pill-hybrid{{background:#ede9fe;color:#5b21b6}}.type-pill-other{{background:#e5e7eb;color:#374151}}.stack{{height:10px;border-radius:999px;overflow:hidden;background:#e5e7eb;display:flex}}.stack-api{{background:#14b8a6}}.stack-ui{{background:#3b82f6}}.stack-hybrid{{background:#8b5cf6}}.stack-other{{background:#9ca3af}}table{{width:100%;border-collapse:collapse}}th,td{{padding:10px;border-bottom:1px solid #e5e7eb;text-align:left;vertical-align:top}}th{{font-size:12px;text-transform:uppercase;color:#6b7280}}details{{margin:0}}ol{{margin:8px 0 0;padding-left:18px}}</style></head><body>");
        sb.AppendLine("<div class='page'>");
        sb.AppendLine(ReportBranding.BuildHeaderCardHtml(
            "Automation Execution Report",
            aggregate.SuiteName,
            aggregate.Environment,
            aggregate.Browser,
            aggregate.StartedAt,
            completedAt,
            duration));
        sb.AppendLine("<div class='grid'>");
        sb.AppendLine($"<div class='card'><div class='muted'>Total</div><div class='metric'>{total}</div></div>");
        sb.AppendLine($"<div class='card'><div class='muted'>Passed</div><div class='metric passed'>{passed}</div></div>");
        sb.AppendLine($"<div class='card'><div class='muted'>Failed</div><div class='metric failed'>{failed}</div></div>");
        sb.AppendLine($"<div class='card'><div class='muted'>Skipped</div><div class='metric skipped'>{skipped}</div></div>");
        sb.AppendLine("</div>");
        sb.AppendLine("<div class='card'>");
        sb.AppendLine("<h2 style='margin-top:0'>Suite Distribution</h2>");
        sb.AppendLine("<div class='type-grid'>");
        sb.AppendLine($"<div class='card'><div class='muted'>API Tests</div><div class='metric type-api'>{apiCount}</div></div>");
        sb.AppendLine($"<div class='card'><div class='muted'>UI Tests</div><div class='metric type-ui'>{uiCount}</div></div>");
        sb.AppendLine($"<div class='card'><div class='muted'>Hybrid Tests</div><div class='metric type-hybrid'>{hybridCount}</div></div>");
        sb.AppendLine($"<div class='card'><div class='muted'>Other Tests</div><div class='metric type-other'>{otherTypeCount}</div></div>");
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

        foreach (var record in records)
        {
            var normalizedType = NormalizeTestType(record.TestType);
            var typeBadgeClass = normalizedType switch
            {
                "API" => "type-pill type-pill-api",
                "UI" => "type-pill type-pill-ui",
                "Hybrid" => "type-pill type-pill-hybrid",
                _ => "type-pill type-pill-other"
            };
            var stepMarkup = record.Steps.Count == 0
                ? "<span class='muted'>No steps</span>"
                : $"<details><summary>{record.Steps.Count} step(s)</summary><ol>{string.Join(string.Empty, record.Steps.Select(step => $"<li>{WebUtility.HtmlEncode(step)}</li>"))}</ol></details>";

            sb.AppendLine("<tr>");
            sb.AppendLine($"<td>{WebUtility.HtmlEncode(record.Name)}</td>");
            sb.AppendLine($"<td><span class='{typeBadgeClass}'>{WebUtility.HtmlEncode(normalizedType)}</span></td>");
            sb.AppendLine($"<td>{WebUtility.HtmlEncode(NormalizePriority(record.Priority))}</td>");
            sb.AppendLine($"<td>{WebUtility.HtmlEncode(record.Outcome)}</td>");
            sb.AppendLine($"<td>{record.Duration}</td>");
            sb.AppendLine($"<td>{WebUtility.HtmlEncode(record.ErrorMessage ?? string.Empty)}</td>");
            sb.AppendLine($"<td>{stepMarkup}</td>");
            sb.AppendLine("</tr>");
        }

        sb.AppendLine("</tbody></table></div></div></body></html>");
        return sb.ToString();
    }

    private static void CleanupOldReports(string reportsDirectory)
    {
        foreach (var file in Directory.GetFiles(reportsDirectory, "execution-report*.html"))
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // Ignore cleanup failures to avoid blocking test execution.
            }
        }
    }

    private static AggregateStore? LoadAggregate(string aggregatePath)
    {
        if (!File.Exists(aggregatePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(aggregatePath);
            return JsonSerializer.Deserialize<AggregateStore>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveAggregate(string aggregatePath, AggregateStore aggregate)
    {
        var json = JsonSerializer.Serialize(aggregate, JsonOptions);
        File.WriteAllText(aggregatePath, json);
    }

    private static string NormalizePriority(string? priority)
    {
        if (string.IsNullOrWhiteSpace(priority))
        {
            return "Unspecified";
        }

        var normalized = priority.Trim();
        return normalized switch
        {
            var value when value.Equals("High", StringComparison.OrdinalIgnoreCase) => "High",
            var value when value.Equals("Medium", StringComparison.OrdinalIgnoreCase) => "Medium",
            var value when value.Equals("Low", StringComparison.OrdinalIgnoreCase) => "Low",
            _ => normalized
        };
    }

    private static string NormalizeTestType(string? testType)
    {
        if (string.IsNullOrWhiteSpace(testType))
        {
            return "Other";
        }

        var normalized = testType.Trim();
        return normalized switch
        {
            var value when value.Equals("API", StringComparison.OrdinalIgnoreCase) => "API",
            var value when value.Equals("UI", StringComparison.OrdinalIgnoreCase) => "UI",
            var value when value.Equals("Hybrid", StringComparison.OrdinalIgnoreCase) => "Hybrid",
            _ => "Other"
        };
    }

    private static void WithGlobalLock(string reportsDirectory, Action action)
    {
        var lockName = "Global\\SeleniumAutomationFramework_Report_Aggregate";
        using var mutex = new Mutex(false, lockName);
        var hasLock = false;

        try
        {
            hasLock = mutex.WaitOne(TimeSpan.FromSeconds(30));
            if (!hasLock)
            {
                throw new TimeoutException("Timed out waiting for report aggregate lock.");
            }

            action();
        }
        finally
        {
            if (hasLock)
            {
                try { mutex.ReleaseMutex(); } catch { }
            }
        }
    }

    private sealed record TestRecord(string Name, string Outcome, string? ErrorMessage, TimeSpan Duration, string Priority, string TestType, List<string> Steps);

    private sealed record TestRecordAggregate(string Name, string Outcome, string? ErrorMessage, TimeSpan Duration, string? Priority, string? TestType, List<string> Steps);

    private sealed record AggregateStore(
        string RunId,
        DateTimeOffset StartedAt,
        DateTimeOffset LastUpdatedUtc,
        string SuiteName,
        string Environment,
        string Browser,
        List<TestRecordAggregate> Records);
}

