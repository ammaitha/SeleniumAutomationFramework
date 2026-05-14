using System.Runtime.CompilerServices;
using NUnit.Framework;

[SetUpFixture]
public sealed class ReportHooks
{
    [ModuleInitializer]
    public static void InitializeReportConfig()
    {
        try
        {
            LoadEnvironmentVariablesFromEnvFile();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ReportHooks] Failed to pre-initialize reporting configuration: {ex.Message}");
        }
    }

    [OneTimeSetUp]
    public void GlobalSetUp()
    {
        var suiteName = Environment.GetEnvironmentVariable("TestSettings__SuiteName") ?? "TestSuite";
        var environment = Environment.GetEnvironmentVariable("TestSettings__Environment") ?? "local";
        var browser = Environment.GetEnvironmentVariable("TestSettings__Browser") ?? "chrome";

        Framework.Reports.ReportManager.InitializeSuite(suiteName, environment, browser);
    }

    [OneTimeTearDown]
    public void GlobalTearDown()
    {
        TryDisposeDrivers();
        Framework.Reports.ReportManager.FinalizeSuite();
    }

    private static void TryDisposeDrivers()
    {
        try
        {
            var driverManagerType = Type.GetType("Framework.Core.Driver.DriverManager, Framework.Core");
            var disposeMethod = driverManagerType?.GetMethod("DisposeDriversForAllThreads");
            disposeMethod?.Invoke(null, null);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ReportHooks] Error disposing WebDriver ThreadLocal instances: {ex.Message}");
        }
    }

    private static void LoadEnvironmentVariablesFromEnvFile()
    {
        var solutionRoot = ResolveSolutionRoot();
        var envFilePath = Path.Combine(solutionRoot, ".env");

        if (!File.Exists(envFilePath))
        {
            return;
        }

        var lines = File.ReadAllLines(envFilePath);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split('=', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                continue;
            }

            var key = parts[0].Trim();
            var value = parts[1].Trim();

            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
            {
                Environment.SetEnvironmentVariable(key, value);
            }
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
}
