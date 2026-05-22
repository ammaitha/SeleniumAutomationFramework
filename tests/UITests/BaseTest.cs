using Framework.Core.Configuration;
using Framework.Core.Driver;
using Framework.Core.Utilities;
using Framework.Data;
using Framework.Reports;
using NUnit.Framework.Interfaces;
using OpenQA.Selenium;
using System.Diagnostics;
using UITests.Pages;

namespace UITests;

public abstract class BaseTest
    : ReportTestBase
{
    protected Serilog.ILogger Logger = Serilog.Log.Logger;
    protected IWebDriver Driver => DriverManager.GetDriver();
    protected WaitHelper Wait = null!;
    private IDisposable? _executionLoggerHandle;
    private readonly Stopwatch _executionTimer = new();
    private DateTimeOffset _testStartedAt;
    private string _activeBrowser = "unknown";
    private string _priority = "Unspecified";
    private string _suiteName = string.Empty;

    protected string ApplicationBaseUrl => ConfigManager.GetString("TestSettings:BaseUrl");
    protected string LoginUrl => ConfigManager.GetString("TestSettings:AppUrl");
    protected string ConfiguredBrowser => ConfigManager.GetString("TestSettings:Browser");
    protected virtual string ExecutionTestType => "UI";

    [SetUp]
    public void SetUp()
    {
        var executionTestType = ExecutionTestType;
        _testStartedAt = DateTimeOffset.Now;
        _executionTimer.Restart();
        _activeBrowser = Environment.GetEnvironmentVariable("TestSettings__Browser") ?? ConfiguredBrowser;
        _priority = GetCurrentPriorityLevel()?.ToString() ?? "Unspecified";
        _suiteName = GetCurrentSuiteName();

        var executionLogger = TestLogger.CreateExecutionLogger(executionTestType, _suiteName, TestContext.CurrentContext.Test.Name);
        Logger = executionLogger.ForContext<BaseTest>();
        _executionLoggerHandle = executionLogger as IDisposable;

        Logger.Information("[{TestType}] Starting test {TestName}", executionTestType, TestContext.CurrentContext.Test.Name);
        RuntimeContext.TestType = executionTestType;
        RuntimeContext.BrowserName = _activeBrowser;
        Logger.Information("[{TestType}] Browser resolved to {Browser}", executionTestType, _activeBrowser);
        BeginReportTest();
        ReportHelper.BeginTest(TestContext.CurrentContext.Test.Name);

        try
        {
            DriverManager.InitializeDriver();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{TestType}] Driver initialization failed for browser {Browser}", executionTestType, _activeBrowser);
            throw new InvalidOperationException(
                $"Failed to initialize {_activeBrowser} WebDriver. " +
                $"Ensure the browser is installed on your system. " +
                $"Error: {ex.Message}", ex);
        }

        Wait = new WaitHelper(Driver, ConfigManager.GetInt("TestSettings:ExplicitWaitSeconds"));

        Driver.Navigate().GoToUrl(LoginUrl);
        Wait.WaitForPageLoaded();
        Logger.Information("[{TestType}] Navigated to login URL {LoginUrl}", executionTestType, LoginUrl);

        // Log actual browser being used (may differ from config if overridden via env var)
        ReportHelper.AddStep($"Browser: {_activeBrowser}");
        ReportHelper.AddStep($"Navigated to {LoginUrl}");
    }

    protected LoginTestData LoadLoginData()
    {
        Logger.Information("[UI] Loading login test data");
        var loginData = LoadTestData<LoginTestData>("loginData.json");

        Assert.That(loginData, Is.Not.Null, "login test data must be valid JSON");

        Assert.That(loginData!.Roles, Is.Not.Null, "roles section is required in loginData.json");
        Assert.That(loginData.Roles.Count, Is.GreaterThan(0),
            "At least one role must be configured in loginData.json");

        return loginData;
    }

    protected HomePageAssertionData LoadHomePageAssertionData()
    {
        Logger.Information("[UI] Loading home page assertion test data");
        var homePageData = LoadTestData<HomePageAssertionData>("homePageData.json");

        Assert.That(homePageData.HomePageHeading.ExpectedHeading, Is.Not.Null.And.Not.Empty,
            "homePageHeading.expectedHeading is required in homePageData.json");
        Assert.That(homePageData.Navigation.EventsPath, Is.Not.Null.And.Not.Empty,
            "navigation.eventsPath is required in homePageData.json");
        Assert.That(homePageData.Navigation.BookingsPath, Is.Not.Null.And.Not.Empty,
            "navigation.bookingsPath is required in homePageData.json");

        return homePageData;
    }

    protected IReadOnlyDictionary<string, string> LoadHomeValidationRow(string sheetName)
    {
        Logger.Information("[UI] Loading home validation data from Excel sheet {SheetName}", sheetName);
        var workbookPath = Path.Combine(AppContext.BaseDirectory, "TestData", "HomeNavigationValidationData.xlsx");
        Assert.That(File.Exists(workbookPath), Is.True,
            $"Excel validation file should exist at {workbookPath}");

        var rows = ExcelDataProvider.ReadSheet(workbookPath, sheetName);
        Assert.That(rows.Count, Is.GreaterThan(0),
            $"Sheet '{sheetName}' must contain at least one data row after headers.");

        return rows[0];
    }

    protected T LoadTestData<T>(string fileName) where T : class
    {
        Logger.Information("[UI] Loading JSON test data from {FileName}", fileName);
        var dataFilePath = Path.Combine(AppContext.BaseDirectory, "TestData", fileName);
        Assert.That(File.Exists(dataFilePath), Is.True, $"test data file should exist at {dataFilePath}");

        var data = JsonDataProvider.Read<T>(dataFilePath);

        Assert.That(data, Is.Not.Null, $"{fileName} must contain valid JSON for {typeof(T).Name}");
        return data!;
    }

    protected void LoginAsRole(string role, LoginTestData? loginData = null)
    {
        var data = loginData ?? LoadLoginData();
        var credentials = ResolveRoleCredentials(data, role);
        ReportHelper.AddStep($"Logging in as role '{role}'");
        var loginPage = new LoginPage(Driver, Wait);
        loginPage.LoginAs(credentials.Email, credentials.Password);
    }

    protected void LoginAsCurrentRole(LoginTestData? loginData = null)
    {
        LoginAsRole(GetCurrentTestRole(), loginData);
    }

    protected RoleCredentials ResolveRoleCredentials(LoginTestData data, string? role = null)
    {
        var resolvedRole = role ?? GetCurrentTestRole();
        var roles = data.Roles
            .ToDictionary(
                kvp => kvp.Key,
                kvp => new RoleCredentials(kvp.Value.Email, kvp.Value.Password),
                StringComparer.OrdinalIgnoreCase);

        var provider = RoleCredentialProvider.Create(
            roles);

        return RoleCredentialResolver.Resolve(resolvedRole, provider);
    }

    [TearDown]
    public void TearDown()
    {
        var executionTestType = ExecutionTestType;
        Logger.Information("[{TestType}] Completing test {TestName}", executionTestType, TestContext.CurrentContext.Test.Name);
        string? screenshotPath = null;
        var failureAttachments = new List<ReportAttachment>();
        var fullClassName = TestContext.CurrentContext.Test.ClassName ?? string.Empty;
        var shortClassName = fullClassName.Contains('.') ? fullClassName[(fullClassName.LastIndexOf('.') + 1)..] : fullClassName;

        try
        {
            if (TestContext.CurrentContext.Result.Outcome.Status == TestStatus.Failed)
            {
                Logger.Warning("[{TestType}] Test {TestName} failed. Capturing artifacts.", executionTestType, TestContext.CurrentContext.Test.Name);
                try
                {
                    screenshotPath = ScreenshotHelper.CaptureScreenshot(
                        Driver,
                        Path.Combine(ReportHelper.GetReportsDirectory(), "screenshots"),
                        $"{shortClassName}_{TestContext.CurrentContext.Test.Name}");
                    Logger.Information("[{TestType}] Screenshot captured at {ScreenshotPath}", executionTestType, screenshotPath);
                }
                catch (InvalidOperationException)
                {
                    // Driver was not initialized, skip screenshot
                }

                try
                {
                    var pageSource = Driver.PageSource;
                    if (!string.IsNullOrWhiteSpace(pageSource))
                    {
                        failureAttachments.Add(new ReportAttachment("Page Source", "text/html", Content: pageSource, FileExtension: "html"));
                    }
                }
                catch (InvalidOperationException)
                {
                    // Driver was not initialized, skip page source
                }
            }

            var outcome = TestContext.CurrentContext.Result.Outcome.Status.ToString();
            var errorMessage = TestContext.CurrentContext.Result.Message;
            _executionTimer.Stop();
            var finishedAt = DateTimeOffset.Now;
            CompleteReportTest(failureAttachments);

            ReportHelper.RecordTestResult(
                TestContext.CurrentContext.Test.Name,
                shortClassName,
                _suiteName,
                outcome,
                _executionTimer.Elapsed,
                _activeBrowser,
                executionTestType,
                _priority,
                _testStartedAt,
                finishedAt,
                errorMessage,
                screenshotPath);

            // Report is generated once per test run in global teardown.
            Logger.Information("[{TestType}] Test {TestName} finished with status {Status} in {DurationMs} ms",
                executionTestType,
                TestContext.CurrentContext.Test.Name,
                outcome,
                _executionTimer.Elapsed.TotalMilliseconds);
        }
        finally
        {
            try
            {
                DriverManager.QuitDriver();
            }
            catch (InvalidOperationException)
            {
                // Driver was never initialized, nothing to quit
            }

            if (TestContext.CurrentContext.Result.Outcome.Status == TestStatus.Failed)
            {
                Logger.Warning("[{TestType}] Test {TestName} failed with message: {Message}",
                    executionTestType,
                    TestContext.CurrentContext.Test.Name,
                    TestContext.CurrentContext.Result.Message);
            }

            _executionLoggerHandle?.Dispose();
            _executionLoggerHandle = null;
        }
    }

    protected sealed class LoginTestData
    {
        public Dictionary<string, Credentials> Roles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public InvalidEmailScenarioData InvalidEmailScenario { get; init; } = new();
        public ShortPasswordScenarioData ShortPasswordScenario { get; init; } = new();
        public EmptyFieldsScenarioData EmptyFieldsScenario { get; init; } = new();
        public WrongPasswordScenarioData WrongPasswordScenario { get; init; } = new();
        public UnregisteredEmailScenarioData UnregisteredEmailScenario { get; init; } = new();
        public AssertionData Assertions { get; init; } = new();

        public sealed class Credentials
        {
            public string Email { get; init; } = string.Empty;
            public string Password { get; init; } = string.Empty;
        }

        public sealed class InvalidEmailScenarioData
        {
            public string Email { get; init; } = string.Empty;
            public string Password { get; init; } = string.Empty;
            public string ExpectedEmailValidationError { get; init; } = string.Empty;
        }

        public sealed class ShortPasswordScenarioData
        {
            public string Email { get; init; } = string.Empty;
            public string Password { get; init; } = string.Empty;
            public string ExpectedPasswordValidationError { get; init; } = string.Empty;
        }

        public sealed class EmptyFieldsScenarioData
        {
            public string ExpectedEmailValidationError { get; init; } = string.Empty;
            public string ExpectedPasswordValidationError { get; init; } = string.Empty;
        }

        public sealed class WrongPasswordScenarioData
        {

            public string Email { get; init; } = string.Empty;
            public string Password { get; init; } = string.Empty;
        }

        public sealed class UnregisteredEmailScenarioData
        {
            public string Email { get; init; } = string.Empty;
            public string Password { get; init; } = string.Empty;

        }

        public sealed class AssertionData
        {
            public string LoginPagePath { get; init; } = string.Empty;
        }
    }

    protected sealed class HomePageAssertionData
    {
        public HeadingData HomePageHeading { get; init; } = new();
        public FeaturedEventsData FeaturedEvents { get; init; } = new();
        public NavigationData Navigation { get; init; } = new();

        public sealed class HeadingData
        {
            public string ExpectedHeading { get; init; } = string.Empty;
        }

        public sealed class FeaturedEventsData
        {
            public int MinimumCardCount { get; init; } = 1;
            public string[] RequiredFields { get; init; } = Array.Empty<string>();
        }

        public sealed class NavigationData
        {
            public string EventsPath { get; init; } = string.Empty;
            public string BookingsPath { get; init; } = string.Empty;
        }
    }

}