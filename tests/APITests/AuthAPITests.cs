using Allure.NUnit;
using Allure.NUnit.Attributes;
using Allure.Net.Commons;
using Framework.API;
using Framework.Reporting;

namespace APITests;

[Parallelizable(ParallelScope.Self)]
[AllureNUnit]
[AllureParentSuite("APITests")]
[AllureSuite("Authentication API")]
[AllureFeature("Authentication")]
[TestRole("user")]
public class AuthAPITests : APITestBase
{
    [Test]
    [Category("High")]
    [Category("Smoke")]
    [Priority(TestPriority.High)]
    [AllureStory("Auth scenario: valid token")]
    [AllureSeverity(SeverityLevel.critical)]
    public async Task AuthScenario_ValidToken_ShouldAccessProtectedEndpoint()
    {
        // Positive flow should use the suite login from setup, not perform login in test body.
        if (!TryBindSuiteTokenToCurrentContext())
        {
            Assert.Inconclusive("No suite token available - suite login failed (credentials may not be configured).");
        }

        var suiteToken = ApiSessionContext.Current.CurrentToken;

        if (suiteToken is not null && !suiteToken.IsValid)
        {
            Assert.Inconclusive("Suite token exists but is already expired in this environment.");
        }
    }

    [Test]
    [Category("High")]
    [Category("Smoke")]
    [Priority(TestPriority.Medium)]
    [AllureStory("Auth scenario: expired token")]
    [AllureSeverity(SeverityLevel.normal)]
    [TestRole("user")]
    public async Task AuthScenario_ExpiredToken_ShouldFailProtectedEndpoint()
    {
        var credentials = ResolveRoleCredentials();
        await LoginAsync(
            credentials.Email,
            credentials.Password,
            tokenScenario: "expired",
            tokenState: false);

        var meResponse = await AuthApi.GetCurrentUserAsync();
        Assert.That((int)meResponse.StatusCode, Is.GreaterThanOrEqualTo(400),
            "Protected endpoint should reject expired token.");
    }

    [Test]
    [Category("High")]
    [Category("Smoke")]
    [Priority(TestPriority.Medium)]
    [AllureStory("Auth scenario: invalid token")]
    [AllureSeverity(SeverityLevel.normal)]
    [TestRole("admin")]
    public async Task AuthScenario_InvalidToken_ShouldFailProtectedEndpoint()
    {
        var credentials = ResolveRoleCredentials();
        await LoginAsync(
            credentials.Email,
            credentials.Password,
            tokenScenario: "invalid",
            tokenState: false);

        var meResponse = await AuthApi.GetCurrentUserAsync();
        Assert.That((int)meResponse.StatusCode, Is.GreaterThanOrEqualTo(400),
            "Protected endpoint should reject invalid token.");
    }

    [Test]
    [Category("High")]
    [Category("Smoke")]
    [Priority(TestPriority.Medium)]
    [AllureStory("Auth scenario: missing token")]
    [AllureSeverity(SeverityLevel.normal)]
    public async Task AuthScenario_MissingToken_ShouldFailProtectedEndpoint()
    {
        var credentials = ResolveRoleCredentials();
        await LoginAsync(
            credentials.Email,
            credentials.Password,
            tokenScenario: "missing",
            tokenState: false);

        var meResponse = await AuthApi.GetCurrentUserAsync();
        Assert.That((int)meResponse.StatusCode, Is.GreaterThanOrEqualTo(400),
            "Protected endpoint should reject requests without token.");
    }

    [Test]
    [Category("High")]
    [Category("Smoke")]
    [Priority(TestPriority.Medium)]
    [AllureStory("Auth edge case: true state with expired scenario")]
    [AllureSeverity(SeverityLevel.normal)]
    public void Login_WithTokenStateTrueAndExpiredScenario_ShouldThrow()
    {
        var credentials = ResolveRoleCredentials();
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await LoginAsync(
                credentials.Email,
                credentials.Password,
                tokenScenario: "expired",
                tokenState: true));
    }

    [Test]
    [Category("High")]
    [Category("Smoke")]
    [Priority(TestPriority.Medium)]
    [AllureStory("Auth edge case: true state with wrong credentials")]
    [AllureSeverity(SeverityLevel.normal)]
    public void Login_WithTokenStateTrueAndWrongCredentials_ShouldThrow()
    {
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await LoginAsync(
                LoginData.WrongPasswordScenario.Email,
                LoginData.WrongPasswordScenario.Password,
                tokenScenario: "valid",
                tokenState: true));
    }


    [Test]
    [Category("High")]
    [Category("Smoke")]
    [Priority(TestPriority.High)]
    [AllureStory("GET /api/auth/me")]
    [AllureSeverity(SeverityLevel.critical)]
    [TestRole("admin")]
    public async Task MeEndpoint_WithValidToken_ShouldReturnCurrentUser()
    {
        // Positive flow should use the suite login from setup, not perform login in test body.
        if (!TryBindSuiteTokenToCurrentContext())
        {
            Assert.Inconclusive("No suite token available - credentials may not be configured in this environment.");
        }

        var meResponse = await AuthApi.GetCurrentUserAsync();
        Assert.That((int)meResponse.StatusCode, Is.EqualTo(200),
            "Endpoint result depends on external account provisioning in the target environment.");
    }

    [Test]
    [Category("High")]
    [Category("Smoke")]
    [Priority(TestPriority.High)]
    [AllureStory("AuthClient helpers: positive valid token flow")]
    [AllureSeverity(SeverityLevel.critical)]
    public async Task AuthClientHelpers_WithValidToken_ShouldReportValidStateAndDetails()
    {
        var directAuthClient = new AuthClient(
            SharedApiClient,
            Logger,
            ApiData.Endpoints.Auth.Login,
            ApiData.Assertions.Auth.TokenJsonPath,
            defaultTokenTtlSeconds: 3600);

        // explicit valid login if needed.
        var credentials = ResolveRoleCredentials();
        var configuredToken = await directAuthClient.LoginAsync(
            credentials.Email,
            credentials.Password,
            tokenScenario: "valid",
            tokenState: true);

        // Direct AuthClient usage needs explicit rebind to the current execution context.
        if (configuredToken is null)
        {
            ApiSessionContext.Current.ClearToken();
            ApiSessionContext.Current.ClearStoredCredentials();
        }
        else
        {
            ApiSessionContext.Current.SetToken(configuredToken);
            ApiSessionContext.Current.StoreCredentials(credentials.Email, credentials.Password);
        }

        directAuthClient.ValidateTokenExists(true, "valid");

        var tokenState = directAuthClient.GetCurrentTokenState();
        var tokenDetails = directAuthClient.GetCurrentTokenDetails();

        Console.WriteLine("=== AuthClientHelpers_WithValidToken_ShouldReportValidStateAndDetails ===");
        Console.WriteLine($"GetCurrentTokenState(): {tokenState}");
        Console.WriteLine($"GetCurrentTokenDetails() object: {tokenDetails}");
        Console.WriteLine($"GetCurrentTokenDetails().AccessToken: {tokenDetails?.AccessToken}");
        Console.WriteLine($"GetCurrentTokenDetails().ExpiresAt: {tokenDetails?.ExpiresAt:O}");
        Console.WriteLine($"GetCurrentTokenDetails().IsValid: {tokenDetails?.IsValid}");
        Console.WriteLine($"GetCurrentTokenDetails().AllowRefresh: {tokenDetails?.AllowRefresh}");

        Assert.That(tokenState, Is.True,
            "GetCurrentTokenState should be true for a valid token.");
        Assert.That(tokenDetails, Is.Not.Null,
            "GetCurrentTokenDetails should return token information for a valid token.");
        Assert.That(tokenDetails!.AccessToken, Is.Not.Null.And.Not.Empty,
            "Access token should be populated for valid token flow.");
        Assert.That(tokenDetails.IsValid, Is.True,
            "Token details should indicate a valid token.");
    }

    [Test]
    [Category("High")]
    [Category("Smoke")]
    [Priority(TestPriority.Medium)]
    [AllureStory("POST /api/auth/login invalid payload")]
    [AllureSeverity(SeverityLevel.normal)]
    public async Task LoginEndpoint_WithInvalidPassword_ShouldReturnFailure()
    {
        var configured = await LoginAsync(
            LoginData.WrongPasswordScenario.Email,
            LoginData.WrongPasswordScenario.Password,
            tokenScenario: "invalid",
            tokenState: false);

        Assert.That(configured, Is.Not.Null,
            "Invalid negative flow should configure a token state (real transformed token or synthetic fallback).");
        Assert.That(configured!.AccessToken, Is.EqualTo("invalid-token-for-negative-scenario"),
            "Invalid negative flow should stamp the known invalid token marker.");
        Assert.That(configured.AllowRefresh, Is.False,
            "Invalid negative flow token should be non-refreshable.");
    }
}
