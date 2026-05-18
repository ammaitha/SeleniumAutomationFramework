using System.Text;
using System.Text.Json;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace Framework.Reports;

public static class ReportManager
{
    private static readonly Lazy<IReporter> ActiveReporter = new(ResolveReporter);
    private static SessionManager? _sessionManager;

    public static void InitializeSuite(string suiteName, string environment, string browser)
    {
        var reportsDirectory = GetReportsDirectory();
        _sessionManager = new SessionManager(reportsDirectory);
        _sessionManager.Initialize();

        Safe(r => r.InitializeSuite(suiteName, environment, browser));
    }

    public static void FinalizeSuite()
    {
        Safe(r => r.FinalizeSuite());
        _sessionManager?.RefreshHeartbeat();
    }

    public static void BeginTest(string testName, string suiteName)
        => Safe(r => r.BeginTest(testName, suiteName));

    public static void AddStep(string message)
        => Safe(r => r.AddStep(message));

    public static void AttachFile(string name, string filePath, string mimeType)
        => Safe(r => r.AttachFile(name, filePath, mimeType));

    public static void AttachContent(string name, string mimeType, string content, string fileExtension)
        => Safe(r => r.AttachContent(name, mimeType, content, fileExtension));

    public static void RecordTestResult(
        string testName,
        string outcome,
        string? errorMessage,
        TimeSpan duration,
        string? priority = null,
        string? testType = null,
        string? screenshotPath = null)
    {
        var normalizedOutcome = NormalizeOutcome(outcome);
        var enrichedErrorMessage = BuildErrorDetails(errorMessage);

        if (string.Equals(normalizedOutcome, TestStatus.Failed.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(enrichedErrorMessage))
            {
                AttachContent("Failure Details", "text/plain", enrichedErrorMessage, "txt");
            }

            if (!string.IsNullOrWhiteSpace(screenshotPath) && File.Exists(screenshotPath))
            {
                AttachFile("Failure Screenshot", screenshotPath, "image/png");
            }
        }

        Safe(r => r.RecordTestResult(testName, normalizedOutcome, enrichedErrorMessage, duration, priority, testType));
    }

    public static string GetReportsDirectory()
    {
        var currentDirectory = new DirectoryInfo(AppContext.BaseDirectory);

        while (currentDirectory is not null)
        {
            var solutionFile = Path.Combine(currentDirectory.FullName, "SeleniumAutomationFramework.sln");
            if (File.Exists(solutionFile))
            {
                var reportDirectory = Path.Combine(currentDirectory.FullName, "reports");
                Directory.CreateDirectory(reportDirectory);
                return reportDirectory;
            }

            currentDirectory = currentDirectory.Parent;
        }

        var fallbackDirectory = Path.Combine(AppContext.BaseDirectory, "reports");
        Directory.CreateDirectory(fallbackDirectory);
        return fallbackDirectory;
    }

    private static IReporter ResolveReporter()
    {
        var requestedReporter = Environment.GetEnvironmentVariable("Reporting__ActiveReporter")
            ?? ReadReporterFromAppSettings()
            ?? "Html";

        if (ReporterFactory.TryCreate(requestedReporter, out var configuredReporter))
        {
            return configuredReporter!;
        }

        if (!string.Equals(requestedReporter, "Html", StringComparison.OrdinalIgnoreCase)
            && ReporterFactory.TryCreate("Html", out var htmlReporter))
        {
            Console.Error.WriteLine(
                $"[ReportManager] Reporter '{requestedReporter}' not found. Falling back to 'Html'.");
            return htmlReporter!;
        }

        if (ReporterFactory.TryCreateFirstAvailable(out var fallbackReporter, out var fallbackName))
        {
            Console.Error.WriteLine(
                $"[ReportManager] Reporter '{requestedReporter}' not found. Falling back to '{fallbackName}'.");
            return fallbackReporter!;
        }

        throw new InvalidOperationException(
            "No reporters discovered. Add at least one IReporter implementation with ReporterAliasAttribute.");
    }

    private static string? ReadReporterFromAppSettings()
    {
        try
        {
            var appSettingsPath = Path.Combine(ResolveSolutionRoot(), "config", "appsettings.json");
            if (!File.Exists(appSettingsPath))
            {
                return null;
            }

            using var stream = File.OpenRead(appSettingsPath);
            using var document = JsonDocument.Parse(stream);

            if (!document.RootElement.TryGetProperty("Reporting", out var reportingSection))
            {
                return null;
            }

            if (!reportingSection.TryGetProperty("ActiveReporter", out var activeReporterElement))
            {
                return null;
            }

            var reporter = activeReporterElement.GetString();
            return string.IsNullOrWhiteSpace(reporter) ? null : reporter;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveSolutionRoot()
    {
        var currentDirectory = new DirectoryInfo(AppContext.BaseDirectory);

        while (currentDirectory is not null)
        {
            var solutionFile = Path.Combine(currentDirectory.FullName, "SeleniumAutomationFramework.sln");
            if (File.Exists(solutionFile))
            {
                return currentDirectory.FullName;
            }

            currentDirectory = currentDirectory.Parent;
        }

        return AppContext.BaseDirectory;
    }

    private static string NormalizeOutcome(string? outcome)
    {
        if (!string.IsNullOrWhiteSpace(outcome))
        {
            return outcome;
        }

        try
        {
            var nunitOutcome = TestContext.CurrentContext?.Result?.Outcome.Status.ToString();
            return string.IsNullOrWhiteSpace(nunitOutcome) ? "Unknown" : nunitOutcome;
        }
        catch
        {
            return "Unknown";
        }
    }

    private static string? BuildErrorDetails(string? providedErrorMessage)
    {
        var message = providedErrorMessage;
        string? stackTrace = null;

        try
        {
            var contextResult = TestContext.CurrentContext?.Result;

            if (string.IsNullOrWhiteSpace(message))
            {
                message = contextResult?.Message;
            }

            stackTrace = contextResult?.StackTrace;
        }
        catch
        {
            // Keep best-effort behavior and fall back to provided values.
        }

        if (string.IsNullOrWhiteSpace(message) && string.IsNullOrWhiteSpace(stackTrace))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(stackTrace))
        {
            return message;
        }

        if (!string.IsNullOrWhiteSpace(message)
            && message.Contains(stackTrace, StringComparison.Ordinal))
        {
            return message;
        }

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(message))
        {
            builder.AppendLine("Message:");
            builder.AppendLine(message);
        }

        if (!string.IsNullOrWhiteSpace(stackTrace))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.AppendLine("StackTrace:");
            builder.AppendLine(stackTrace);
        }

        return builder.ToString().Trim();
    }

    private static void Safe(Action<IReporter> action)
    {
        try
        {
            action(ActiveReporter.Value);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ReportManager] Reporter call failed: {ex.Message}");
        }
    }
}
