using System.Text.Json;

namespace Framework.Reports;

internal sealed class SessionManager
{
    private readonly string _reportsDirectory;
    private readonly string _sessionMarkerPath;
    private readonly string _aggregatePath;
    private static readonly TimeSpan SessionExpirationWindow = ResolveSessionExpirationWindow();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private SessionMarker? _currentMarker;
    private bool _isNewSession;

    public bool IsNewSession => _isNewSession;

    public SessionManager(string reportsDirectory)
    {
        _reportsDirectory = reportsDirectory;
        _sessionMarkerPath = Path.Combine(reportsDirectory, ".execution-session-marker.json");
        _aggregatePath = Path.Combine(reportsDirectory, ".execution-report-aggregate.json");
    }

    public void Initialize()
    {
        _currentMarker = LoadSessionMarker();
        _isNewSession = ShouldStartNewSession(_currentMarker);

        if (_isNewSession)
        {
            CleanupOldLogs();
            CleanupOldScreenshots();
            CleanupReporterOutput();
        }

        UpdateSessionMarker(_isNewSession);
    }

    public void RefreshHeartbeat()
    {
        UpdateSessionMarker(isNewSession: false);
    }

    private bool ShouldStartNewSession(SessionMarker? marker)
    {
        if (HasRecentAggregateHeartbeat())
        {
            return false;
        }

        if (marker is null)
        {
            return true;
        }

        var age = DateTimeOffset.UtcNow - marker.UpdatedAtUtc;
        return age > SessionExpirationWindow;
    }

    private bool HasRecentAggregateHeartbeat()
    {
        try
        {
            if (!File.Exists(_aggregatePath))
            {
                return false;
            }

            var lastWrite = File.GetLastWriteTimeUtc(_aggregatePath);
            var age = DateTimeOffset.UtcNow - new DateTimeOffset(lastWrite, TimeSpan.Zero);
            return age <= SessionExpirationWindow;
        }
        catch
        {
            return false;
        }
    }

    private static TimeSpan ResolveSessionExpirationWindow()
    {
        const int defaultSeconds = 15;
        var configuredValue = Environment.GetEnvironmentVariable("Reporting__SessionExpirationSeconds");

        if (int.TryParse(configuredValue, out var seconds) && seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return TimeSpan.FromSeconds(defaultSeconds);
    }

    private void UpdateSessionMarker(bool isNewSession)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var marker = new SessionMarker(
                SessionId: isNewSession ? Guid.NewGuid().ToString("N") : (_currentMarker?.SessionId ?? Guid.NewGuid().ToString("N")),
                UpdatedAtUtc: now);

            var json = JsonSerializer.Serialize(marker, JsonOptions);
            File.WriteAllText(_sessionMarkerPath, json);
            _currentMarker = marker;
        }
        catch
        {
            // Ignore marker update failures
        }
    }

    private SessionMarker? LoadSessionMarker()
    {
        try
        {
            if (!File.Exists(_sessionMarkerPath))
            {
                return null;
            }

            var json = File.ReadAllText(_sessionMarkerPath);
            return JsonSerializer.Deserialize<SessionMarker>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private void CleanupOldLogs()
    {
        var logsDirectory = Path.Combine(_reportsDirectory, "logs");
        if (!Directory.Exists(logsDirectory))
        {
            return;
        }

        try
        {
            Directory.Delete(logsDirectory, recursive: true);
            Directory.CreateDirectory(logsDirectory);
        }
        catch
        {
            // Ignore cleanup failures to avoid blocking test execution
        }
    }

    private void CleanupOldScreenshots()
    {
        var screenshotsDirectory = Path.Combine(_reportsDirectory, "screenshots");
        if (!Directory.Exists(screenshotsDirectory))
        {
            return;
        }

        try
        {
            Directory.Delete(screenshotsDirectory, recursive: true);
            Directory.CreateDirectory(screenshotsDirectory);
        }
        catch
        {
            // Ignore cleanup failures to avoid blocking test execution
        }
    }

    private void CleanupReporterOutput()
    {
        try
        {
            // Clear HTML report artifacts
            var htmlAggregateFile = Path.Combine(_reportsDirectory, ".execution-report-aggregate.json");
            if (File.Exists(htmlAggregateFile))
            {
                File.Delete(htmlAggregateFile);
            }

            var htmlReportFile = Path.Combine(_reportsDirectory, "execution-report.html");
            if (File.Exists(htmlReportFile))
            {
                File.Delete(htmlReportFile);
            }

            // Clear Allure result artifacts
            var allurePatterns = new[] { "*-result.json", "*-attachment*", "environment.properties" };
            foreach (var pattern in allurePatterns)
            {
                foreach (var file in Directory.GetFiles(_reportsDirectory, pattern, SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // Ignore individual file deletion failures
                    }
                }
            }
        }
        catch
        {
            // Ignore cleanup failures to avoid blocking test execution
        }
    }

    private sealed record SessionMarker(string SessionId, DateTimeOffset UpdatedAtUtc);
}
