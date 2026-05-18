using Framework.Reports;
using Serilog;

namespace Framework.Core.Utilities;

/// <summary>
/// Factory for per-test execution Serilog instances with error handling.
/// Creates a new logger for each test execution with a unique file name that includes
/// the test type, suite name, and test name.
/// Includes fallback to console-only logging if file system access fails.
/// </summary>
public static class TestLogger
{
    /// <summary>
    /// Creates a per-test execution logger with lifecycle management.
    /// The returned logger should be disposed or reassigned when test execution completes.
    /// </summary>
    public static ILogger CreateExecutionLogger(string testType, string suiteName, string testName)
    {
        var logsDirectory = Path.Combine(ReportHelper.GetReportsDirectory(), "logs");

        try
        {
            Directory.CreateDirectory(logsDirectory);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create logs directory at {LogsDirectory}. Using fallback.", logsDirectory);
            // Fallback to temp directory if creation fails
            logsDirectory = Path.Combine(Path.GetTempPath(), "SeleniumTests", "logs");
            try
            {
                Directory.CreateDirectory(logsDirectory);
            }
            catch (Exception fallbackEx)
            {
                Log.Fatal(fallbackEx, "Failed to create fallback logs directory at {LogsDirectory}. Logging to console only.", logsDirectory);
                // If even fallback fails, return console-only logger
                return new LoggerConfiguration()
                    .MinimumLevel.Information()
                    .Enrich.FromLogContext()
                    .WriteTo.Console()
                    .CreateLogger();
            }
        }

        var safeType = SanitizeForFileName(testType);
        var safeSuite = SanitizeForFileName(suiteName);
        var safeTest = SanitizeForFileName(testName);
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var fileName = $"{timestamp}_{safeType}_{safeSuite}_{safeTest}.log";

        try
        {
            return new LoggerConfiguration()
                .MinimumLevel.Information()
                .Enrich.FromLogContext()
                .WriteTo.Console()
                .WriteTo.File(
                    path: Path.Combine(logsDirectory, fileName),
                    rollingInterval: RollingInterval.Infinite,
                    retainedFileCountLimit: 50,
                    shared: false)
                .CreateLogger();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to configure file logging for {FileName}. Using console-only logger.", fileName);
            // Fallback to console-only if file configuration fails
            return new LoggerConfiguration()
                .MinimumLevel.Information()
                .Enrich.FromLogContext()
                .WriteTo.Console()
                .CreateLogger();
        }
    }

    private static string SanitizeForFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unknown";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var normalized = new string(value.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "Unknown" : normalized;
    }
}
