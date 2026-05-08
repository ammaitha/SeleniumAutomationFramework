using Allure.NUnit;
using Allure.NUnit.Attributes;
using UITests.Pages;
using Framework.Core.Configuration;
using Framework.Reporting;

namespace UITests;

[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
[Parallelizable(ParallelScope.All)]
[AllureNUnit]
[AllureParentSuite("UITests")]
[AllureSuite("Login")]
[AllureFeature("Authentication")]
public class LoginTests : BaseTest
{
    // Loaded once per test — all inputs and expected values come from loginData.json
    private LoginTestData _data = null!;

    [SetUp]
    public void SetUpTests()
    {
        _data = LoadLoginData();
    }

    [Test]
    [Category("High")]
    [Category("Smoke")]
    [Priority(TestPriority.High)]
    [AllureStory("Login page field visibility")]
    public void LoginPage_WithoutSigningIn_ShouldDisplayAllRequiredFields()
    {
        var loginPage = new LoginPage(Driver, Wait);

        ReportHelper.AddStep("Verifying login page is loaded and all required fields are visible");
        Assert.That(Driver.Url, Does.Contain("/login"),
            "Expected browser to be on login page.");
        Assert.That(loginPage.IsEmailFieldDisplayed(), Is.True,
            "Email field should be visible on login page.");
        Assert.That(loginPage.IsPasswordFieldDisplayed(), Is.True,
            "Password field should be visible on login page.");
        Assert.That(loginPage.IsLoginButtonDisplayed(), Is.True,
            "Login button should be visible on login page.");

        var loginButtonText = loginPage.GetLoginButtonText();
        Assert.That(loginButtonText.Contains("Sign In", StringComparison.OrdinalIgnoreCase), Is.True,
            "Login button text should indicate sign-in action.");
    }

    [Test]
    [TestCase("user", Category = "user")]
    [TestCase("admin", Category = "admin")]
    [TestCase("organizer", Category = "organizer")]
    [TestCase("viewer", Category = "viewer")]
    [Category("High")]
    [Category("Smoke")]
    [Priority(TestPriority.High)]
    [AllureStory("Role-based login")]
    public void Login_WithConfiguredRoleCredentials_ShouldAuthenticate(string role)
    {
        ReportHelper.AddStep($"Entering credentials for role '{role}'");
        LoginAsRole(role, _data);
        ReportHelper.AddStep("Verifying home page and login status");
        var homePage = new HomePage(Driver, Wait);

        Assert.That(homePage.IsUserLoggedIn(), Is.True, "User is not logged in based on home page signals.");
        Assert.That(homePage.IsHomePageLoaded(), Is.True, "User did not land on home page after login.");
        Assert.That(homePage.GetCurrentUrl(), Does.Not.Contain(_data.Assertions.LoginPagePath),
            "User URL still indicates login page.");

        var expectedDomain = new Uri(ConfigManager.GetString("TestSettings:BaseUrl")).Host;
        Assert.That(homePage.GetCurrentUrl(), Does.Contain(expectedDomain),
            "User is not on the expected application domain.");
        ReportHelper.AddStep("Login test completed successfully");
    }

    [Test]
    [Category("High")]
    [Category("Smoke")]
    [Priority(TestPriority.Medium)]
    [AllureStory("Email validation")]
    public void Login_WithInvalidEmailFormat_ShouldShowEmailValidationError()
    {
        var scenario = _data.InvalidEmailScenario;
        ReportHelper.AddStep($"Entering invalid email: '{scenario.Email}' with password: '{scenario.Password}'");
        var loginPage = new LoginPage(Driver, Wait);
        loginPage.AttemptToLoginWithInvalidCreds(scenario.Email, scenario.Password);

        ReportHelper.AddStep("Verifying inline email validation error is displayed");
        Assert.That(loginPage.IsEmailValidationErrorDisplayed(), Is.True,
            "Email validation error should be visible for invalid email format");

        var actualError = loginPage.GetEmailValidationErrorText();
        ReportHelper.AddStep($"Email validation error — expected: '{scenario.ExpectedEmailValidationError}', actual: '{actualError}'");
        Assert.That(actualError, Is.EqualTo(scenario.ExpectedEmailValidationError),
            "Email validation error text did not match expected value from data file");

        ReportHelper.AddStep("Verifying Login Button is displayed as we are on login page.");
        Assert.That(loginPage.IsLoginButtonDisplayed(), Is.True, "Login button should be displayed when login fails");
    }

    [Test]
    [Category("High")]
    [Category("Smoke")]
    [Priority(TestPriority.Medium)]
    [AllureStory("Password validation")]
    public void Login_WithShortPassword_ShouldShowPasswordValidationError()
    {
        var scenario = _data.ShortPasswordScenario;
        ReportHelper.AddStep($"Entering email: '{scenario.Email}' with short password: '{scenario.Password}'");
        var loginPage = new LoginPage(Driver, Wait);
        loginPage.AttemptToLoginWithInvalidCreds(scenario.Email, scenario.Password);

        ReportHelper.AddStep("Verifying inline password validation error is displayed");
        Assert.That(loginPage.IsPasswordValidationErrorDisplayed(), Is.True,
            "Password validation error should be visible for password shorter than 6 characters");

        var actualError = loginPage.GetPasswordValidationErrorText();
        ReportHelper.AddStep($"Password validation error — expected: '{scenario.ExpectedPasswordValidationError}', actual: '{actualError}'");
        Assert.That(actualError, Is.EqualTo(scenario.ExpectedPasswordValidationError),
            "Password validation error text did not match expected value from data file");

        ReportHelper.AddStep("Verifying Login Button is displayed as we are on login page.");
        Assert.That(loginPage.IsLoginButtonDisplayed(), Is.True, "Login button should be displayed when login fails");
    }

    [Test]
    [Category("High")]
    [Category("Smoke")]
    [Priority(TestPriority.High)]
    [AllureStory("Required field validation")]
    public void Login_WithEmptyFields_ShouldShowBothValidationErrors()
    {
        var scenario = _data.EmptyFieldsScenario;
        ReportHelper.AddStep("Submitting the login form with no credentials entered");
        var loginPage = new LoginPage(Driver, Wait);
        loginPage.SubmitEmptyForm();

        ReportHelper.AddStep($"Verifying email validation error — expected: '{scenario.ExpectedEmailValidationError}'");
        Assert.That(loginPage.IsEmailValidationErrorDisplayed(), Is.True,
            "Email validation error should appear when email is empty");
        Assert.That(loginPage.GetEmailValidationErrorText(), Is.EqualTo(scenario.ExpectedEmailValidationError),
            "Email validation error text did not match expected value from data file");

        ReportHelper.AddStep($"Verifying password validation error — expected: '{scenario.ExpectedPasswordValidationError}'");
        Assert.That(loginPage.IsPasswordValidationErrorDisplayed(), Is.True,
            "Password validation error should appear when password is empty");
        Assert.That(loginPage.GetPasswordValidationErrorText(), Is.EqualTo(scenario.ExpectedPasswordValidationError),
            "Password validation error text did not match expected value from data file");

        ReportHelper.AddStep("Verifying Login Button is displayed as we are on login page.");
        Assert.That(loginPage.IsLoginButtonDisplayed(), Is.True, "Login button should be displayed when login fails");
    }

    [Test]
    [Category("Sanity")]
    [Priority(TestPriority.Low)]
    [AllureStory("Wrong password rejection")]
    public void Login_WithWrongPassword_ShouldBeOnLoginPage()
    {
        var scenario = _data.WrongPasswordScenario;
        ReportHelper.AddStep($"Entering valid email with wrong password: '{scenario.Password}'");
        var loginPage = new LoginPage(Driver, Wait);
        loginPage.AttemptToLoginWithInvalidCreds(scenario.Email, scenario.Password);

        ReportHelper.AddStep("Verifying Login Button is displayed as we are on login page.");
        Assert.That(loginPage.IsLoginButtonDisplayed(), Is.True, "Login button should be displayed when login fails");
    }

    [Test]
    [Category("Sanity")]
    [Priority(TestPriority.Low)]
    [AllureStory("Unregistered email rejection")]
    public void Login_WithUnregisteredEmail_ShouldBeOnLoginPage()
    {
        var scenario = _data.UnregisteredEmailScenario;
        ReportHelper.AddStep($"Entering unregistered email: '{scenario.Email}' with password: '{scenario.Password}'");
        var loginPage = new LoginPage(Driver, Wait);
        loginPage.AttemptToLoginWithInvalidCreds(scenario.Email, scenario.Password);

        ReportHelper.AddStep("Verifying Login Button is displayed as we are on login page.");
        Assert.That(loginPage.IsLoginButtonDisplayed(), Is.True, "Login button should be displayed when login fails");
    }
}
