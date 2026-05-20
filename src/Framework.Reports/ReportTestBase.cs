using System.Reflection;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace Framework.Reports;

public abstract class ReportTestBase
{
    private readonly AsyncLocal<DateTimeOffset?> _testStart = new();

    protected void BeginReportTest()
    {
        _testStart.Value = DateTimeOffset.UtcNow;
    }

    protected void CompleteReportTest(IEnumerable<ReportAttachment>? failureAttachments = null)
    {
        try
        {
            var duration = DateTimeOffset.UtcNow - (_testStart.Value ?? DateTimeOffset.UtcNow);
            var outcome = TestContext.CurrentContext.Result.Outcome.Status;
            var executionTestType = ResolveExecutionTestType();

            ReportManager.AddStep($"Browser={RuntimeContext.BrowserName}");
            ReportManager.AddStep($"TestType={executionTestType}");
            ReportManager.AddStep($"Duration={duration.TotalSeconds:F2}s");
            ReportManager.AddStep($"Outcome={outcome}");

            try
            {
                ReportManager.AddStep($"Role={GetCurrentTestRole()}");
            }
            catch (InvalidOperationException)
            {
                ReportManager.AddStep("Role=none");
            }

            if (outcome == TestStatus.Failed)
            {
                foreach (var attachment in failureAttachments ?? [])
                {
                    Attach(attachment);
                }
            }

            if (outcome != TestStatus.Passed)
            {
                var apiExchange = RuntimeContext.GetLastApiExchange();
                if (apiExchange is not null)
                {
                    ReportHelper.AttachContent("API Request", "text/plain", apiExchange.Request, "txt");
                    ReportHelper.AttachContent("API Response", "text/plain", apiExchange.Response, "txt");
                }
            }
        }
        finally
        {
            RuntimeContext.ClearTestScope();
        }
    }

    protected TestPriority? GetCurrentPriorityLevel()
    {
        var method = ResolveTestMethod();
        return method?.GetCustomAttribute<PriorityAttribute>(true)?.Level
            ?? GetType().GetCustomAttribute<PriorityAttribute>(true)?.Level;
    }

    protected string GetCurrentSuiteName()
    {
        return GetType().Name;
    }

    protected string GetCurrentTestRole()
    {
        var method = ResolveTestMethod();

        var role = method?.GetCustomAttribute<TestRoleAttribute>(true)?.Role;
        if (!string.IsNullOrWhiteSpace(role))
        {
            return role;
        }

        role = GetType().GetCustomAttribute<TestRoleAttribute>(true)?.Role;
        if (!string.IsNullOrWhiteSpace(role))
        {
            return role;
        }

        role = Environment.GetEnvironmentVariable("TEST_EXECUTION_ROLE");
        if (!string.IsNullOrWhiteSpace(role))
        {
            return role.Trim().ToLowerInvariant();
        }

        throw new InvalidOperationException(
            $"Test '{GetType().Name}.{method?.Name}' does not specify a role for execution. " +
            "Provide TEST_EXECUTION_ROLE or [TestRole] attribute.");
    }

    protected sealed record ReportAttachment(string Name, string ContentType, string? FilePath = null, string? Content = null, string FileExtension = "txt");

    private MethodInfo? ResolveTestMethod()
    {
        var methodName = TestContext.CurrentContext.Test.MethodName;
        if (string.IsNullOrWhiteSpace(methodName))
        {
            return null;
        }

        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        return GetType().GetMethods(flags)
            .FirstOrDefault(method => string.Equals(method.Name, methodName, StringComparison.Ordinal));
    }

    public string ResolveExecutionTestType()
    {
        var className = TestContext.CurrentContext.Test.ClassName ?? string.Empty;

        if (className.Contains("APITests", StringComparison.OrdinalIgnoreCase))
        {
            return "API";
        }

        if (className.Contains("UITests", StringComparison.OrdinalIgnoreCase))
        {
            return "UI";
        }

        if (className.Contains("HybridTests", StringComparison.OrdinalIgnoreCase))
        {
            return "Hybrid";
        }

        return RuntimeContext.TestType;
    }

    private static void Attach(ReportAttachment attachment)
    {
        if (!string.IsNullOrWhiteSpace(attachment.FilePath))
        {
            ReportHelper.AttachFile(attachment.Name, attachment.FilePath, attachment.ContentType);
            return;
        }

        if (!string.IsNullOrWhiteSpace(attachment.Content))
        {
            ReportHelper.AttachContent(attachment.Name, attachment.ContentType, attachment.Content, attachment.FileExtension);
        }
    }
}
