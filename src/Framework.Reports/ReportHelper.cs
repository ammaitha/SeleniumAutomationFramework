namespace Framework.Reports;

public static class ReportHelper
{
    public static void AttachFile(string name, string filePath, string contentType)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        ReportManager.AttachFile(name, filePath, contentType);
    }

    public static void AttachContent(string name, string contentType, string content, string fileExtension)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        ReportManager.AttachContent(name, contentType, content, fileExtension);
    }

    public static void AddStep(string stepMessage, bool createStep = true)
    {
        if (string.IsNullOrWhiteSpace(stepMessage))
        {
            return;
        }

        ReportManager.AddStep(stepMessage);
    }

    public static void BeginTest(string testName)
    {
        ReportManager.BeginTest(testName, testName);
    }

    public static string GetReportsDirectory()
    {
        return ReportManager.GetReportsDirectory();
    }

    public static void RecordTestResult(
        string testName,
        string className,
        string suiteName,
        string status,
        TimeSpan duration,
        string browser,
        string testType,
        string priority,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        string? errorMessage = null,
        string? screenshotPath = null)
    {
        ReportManager.RecordTestResult(testName, status, errorMessage, duration, priority, testType, screenshotPath);
    }

    public static string GenerateHtmlReport()
    {
        ReportManager.FinalizeSuite();
        return string.Empty;
    }
}
