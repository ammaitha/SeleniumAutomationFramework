namespace Framework.Reports;

public interface IReporter
{
    void InitializeSuite(string suiteName, string environment, string browser);
    void BeginTest(string testName, string suiteName);
    void AddStep(string message);
    void AttachFile(string name, string filePath, string mimeType);
    void AttachContent(string name, string mimeType, string content, string fileExtension);
    void RecordTestResult(string testName, string outcome, string? errorMessage, TimeSpan duration, string? priority = null, string? testType = null);
    void FinalizeSuite();
}
