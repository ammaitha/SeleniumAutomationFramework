using NUnit.Framework.Interfaces;
using Framework.Core.Configuration;
using Framework.Core.Utilities;
using Framework.Data;
using Framework.API;
using Framework.API.Clients;
using Framework.Reports;
using Framework.Contracts;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace APITests;

public abstract class APITestBase : ReportTestBase
{
    protected Serilog.ILogger Logger = Serilog.Log.Logger;
    protected HttpClient SharedHttpClient = null!;
    protected APIClient SharedApiClient = null!;
    protected AuthClient SharedAuthClient = null!;
    protected AuthApiClient AuthApi = null!;
    protected EventsApiClient EventsApi = null!;
    protected BookingsApiClient BookingsApi = null!;
    protected ApiSuiteData ApiData = null!;
    protected LoginDataModel LoginData = null!;
    protected RoleCredentialProvider RoleCredentials = null!;

    private IDisposable? _executionLoggerHandle;
    private readonly Dictionary<string, TokenState?> _suiteTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Email, string Password)> _suiteCredentialsByRole = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _suiteLock = new(1, 1);

    private readonly Stopwatch _executionTimer = new();
    private DateTimeOffset _testStartedAt;
    private string _priority = "Unspecified";
    private string _suiteName = string.Empty;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var apiBaseUrl = ConfigManager.GetString("Api:BaseUrl");
        Assert.That(apiBaseUrl, Is.Not.Null.And.Not.Empty, "Api:BaseUrl configuration is required. Ensure it is set in appsettings.json or environment variables.");

        SharedHttpClient = new HttpClient
        {
            BaseAddress = new Uri(apiBaseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };
        Assert.That(SharedHttpClient, Is.Not.Null, "SharedHttpClient initialization failed.");
        Assert.That(SharedHttpClient.BaseAddress, Is.Not.Null, "SharedHttpClient BaseAddress should not be null.");

        SharedHttpClient.DefaultRequestHeaders.Accept.Clear();
        SharedHttpClient.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        LoginData = LoadLoginData();
        Assert.That(LoginData, Is.Not.Null, "LoginData should not be null after loading from loginData.json.");
        Assert.That(LoginData.ApiAuth, Is.Not.Null, "LoginData.ApiAuth should not be null.");
        RoleCredentials = BuildRoleCredentialProvider(LoginData);
        Assert.That(RoleCredentials, Is.Not.Null, "Role credential provider should be initialized.");
        
        ApiData = LoadApiTestData(LoginData.ApiAuth);
        Assert.That(ApiData, Is.Not.Null, "ApiData should not be null after merging from all data sources.");
        Assert.That(ApiData.Endpoints, Is.Not.Null, "ApiData.Endpoints should not be null.");
        Assert.That(ApiData.Assertions, Is.Not.Null, "ApiData.Assertions should not be null.");
        
        SharedApiClient = new APIClient(SharedHttpClient);
        Assert.That(SharedApiClient, Is.Not.Null, "SharedApiClient initialization failed.");
        
        SharedApiClient.ShowBearerToken = ConfigManager.GetBool("Api:ShowBearerToken");
        
        // Create AuthClient for in-memory, session-scoped token management
        SharedAuthClient = new AuthClient(
            SharedApiClient,
            Logger,
            ApiData.Endpoints.Auth.Login,
            ApiData.Assertions.Auth.TokenJsonPath,
            defaultTokenTtlSeconds: 3600);
        Assert.That(SharedAuthClient, Is.Not.Null, "SharedAuthClient initialization failed.");
        
        // Initialize API clients. Token scenario is controlled by tests via LoginAsync helper.
        AuthApi = new AuthApiClient(
            SharedApiClient,
            Logger,
            ApiData.Endpoints.Auth);
        Assert.That(AuthApi, Is.Not.Null, "AuthApiClient initialization failed.");
        Assert.That(ApiData.Endpoints.Auth, Is.Not.Null, "ApiData.Endpoints.Auth should not be null.");
        
        EventsApi = new EventsApiClient(
            SharedApiClient,
            Logger,
            ApiData.Endpoints.Events);
        Assert.That(EventsApi, Is.Not.Null, "EventsApiClient initialization failed.");
        Assert.That(ApiData.Endpoints.Events, Is.Not.Null, "ApiData.Endpoints.Events should not be null.");
        
        BookingsApi = new BookingsApiClient(
            SharedApiClient,
            Logger,
            ApiData.Endpoints.Bookings);
        Assert.That(BookingsApi, Is.Not.Null, "BookingsApiClient initialization failed.");
        Assert.That(ApiData.Endpoints.Bookings, Is.Not.Null, "ApiData.Endpoints.Bookings should not be null.");

    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        try
        {
            // Clear session context to remove any stored tokens from memory
            ApiSessionContext.Current.ClearToken();
            ApiSessionContext.ClearCurrentContext();
            Logger.Information("API session context cleared.");
        }
        catch (Exception ex)
        {
            Logger.Warning("Error clearing API session context: {Message}", ex.Message);
        }

        try
        {
            if (SharedApiClient != null)
            {
                SharedApiClient.Dispose();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error disposing SharedApiClient: {Message}", ex.Message);
        }

        try
        {
            if (SharedHttpClient != null)
            {
                SharedHttpClient.Dispose();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error disposing SharedHttpClient: {Message}", ex.Message);
        }
    }

    private void RebindApiDependenciesWithExecutionLogger()
    {
        Assert.That(ApiData, Is.Not.Null, "ApiData should be initialized before rebinding API dependencies.");
        Assert.That(SharedApiClient, Is.Not.Null, "SharedApiClient should be initialized before rebinding API dependencies.");
        Assert.That(Logger, Is.Not.Null, "Logger should be initialized before rebinding API dependencies.");

        // Bind the per-test Serilog logger to the shared HTTP client so request/response
        // details (method, URL, status code) are printed to console/file for every call.
        SharedApiClient.SerilogLogger = Logger;

        // Recreate logger-dependent API helpers per test so request/response entries
        // are written to the current execution log file without changing API behavior.
        SharedAuthClient = new AuthClient(
            SharedApiClient,
            Logger,
            ApiData.Endpoints.Auth.Login,
            ApiData.Assertions.Auth.TokenJsonPath,
            defaultTokenTtlSeconds: 3600);

        AuthApi = new AuthApiClient(
            SharedApiClient,
            Logger,
            ApiData.Endpoints.Auth);

        EventsApi = new EventsApiClient(
            SharedApiClient,
            Logger,
            ApiData.Endpoints.Events);

        BookingsApi = new BookingsApiClient(
            SharedApiClient,
            Logger,
            ApiData.Endpoints.Bookings);
    }

    private ApiSuiteData LoadApiTestData(ApiAuthData authData)
    {
        Assert.That(authData, Is.Not.Null, "ApiAuthData cannot be null. Ensure apiAuth section exists in loginData.json.");
        Assert.That(authData.Endpoints, Is.Not.Null, "authData.Endpoints cannot be null.");
        Assert.That(authData.Assertions, Is.Not.Null, "authData.Assertions cannot be null.");

        ValidateAuthEndpoints(authData.Endpoints, "loginData.json");
        Assert.That(authData.Assertions.TokenJsonPath, Is.Not.Null.And.Not.Empty, "apiAuth.assertions.tokenJsonPath is required in loginData.json");
        Assert.That(authData.Assertions.CurrentUserEmailJsonPath, Is.Not.Null.And.Not.Empty, "apiAuth.assertions.currentUserEmailJsonPath is required in loginData.json");

        var eventData = LoadEventTestData();
        Assert.That(eventData, Is.Not.Null, "EventApiDataModel should not be null after loading from eventData.json.");
        Assert.That(eventData.Endpoints, Is.Not.Null, "eventData.Endpoints should not be null.");
        Assert.That(eventData.Events, Is.Not.Null, "eventData.Events should not be null.");
        Assert.That(eventData.Assertions, Is.Not.Null, "eventData.Assertions should not be null.");
        
        var bookingData = LoadBookingTestData();
        Assert.That(bookingData, Is.Not.Null, "BookingApiDataModel should not be null after loading from bookingData.json.");
        Assert.That(bookingData.Endpoints, Is.Not.Null, "bookingData.Endpoints should not be null.");
        Assert.That(bookingData.Bookings, Is.Not.Null, "bookingData.Bookings should not be null.");
        Assert.That(bookingData.Assertions, Is.Not.Null, "bookingData.Assertions should not be null.");

        return new ApiSuiteData
        {
            Endpoints = BuildEndpointData(authData.Endpoints, eventData.Endpoints, bookingData.Endpoints),
            Events = eventData.Events,
            Bookings = bookingData.Bookings,
            Queries = new ApiSuiteData.QueryData
            {
                Events = eventData.Queries,
                Bookings = bookingData.Queries
            },
            Assertions = new ApiSuiteData.AssertionData
            {
                Auth = new ApiSuiteData.AssertionData.AuthAssertionData
                {
                    TokenJsonPath = authData.Assertions.TokenJsonPath,
                    CurrentUserEmailJsonPath = authData.Assertions.CurrentUserEmailJsonPath
                },
                Events = eventData.Assertions,
                Bookings = bookingData.Assertions
            }
        };
    }

    private EventApiDataModel LoadEventTestData()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "eventData.json");
        Assert.That(File.Exists(path), Is.True, $"eventData file should exist at {path}. Ensure the file is present and copied to the output directory.");

        var data = JsonDataProvider.Read<EventApiDataModel>(path);
        Assert.That(data, Is.Not.Null, "eventData.json must be valid JSON and deserializable to EventApiDataModel.");

        Assert.That(data!.Endpoints, Is.Not.Null, "data.Endpoints cannot be null in eventData.json.");
        ValidateEventEndpoints(data.Endpoints, "eventData.json");
        
        Assert.That(data.Events, Is.Not.Null, "data.Events cannot be null in eventData.json.");
        Assert.That(data.Events.CreatePayload, Is.Not.Null.And.Property("HasValues").True, "events.createPayload with valid structure is required in eventData.json");
        Assert.That(data.Events.UpdatePayload, Is.Not.Null.And.Property("HasValues").True, "events.updatePayload with valid structure is required in eventData.json");
        Assert.That(data.Events.InvalidCreatePayload, Is.Not.Null.And.Property("HasValues").True, "events.invalidCreatePayload with valid structure is required in eventData.json");
        
        Assert.That(data.Assertions, Is.Not.Null, "data.Assertions cannot be null in eventData.json.");
        Assert.That(data.Assertions.CreatedEventIdJsonPath, Is.Not.Null.And.Not.Empty, "assertions.createdEventIdJsonPath is required in eventData.json");
        Assert.That(data.Queries, Is.Not.Null, "data.Queries cannot be null in eventData.json.");

        return data;
    }

    private BookingApiDataModel LoadBookingTestData()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "bookingData.json");
        Assert.That(File.Exists(path), Is.True, $"bookingData file should exist at {path}. Ensure the file is present and copied to the output directory.");

        var data = JsonDataProvider.Read<BookingApiDataModel>(path);
        Assert.That(data, Is.Not.Null, "bookingData.json must be valid JSON and deserializable to BookingApiDataModel.");

        Assert.That(data!.Endpoints, Is.Not.Null, "data.Endpoints cannot be null in bookingData.json.");
        ValidateBookingEndpoints(data.Endpoints, "bookingData.json");
        
        Assert.That(data.Bookings, Is.Not.Null, "data.Bookings cannot be null in bookingData.json.");
        Assert.That(data.Bookings.SupportingEventPayload, Is.Not.Null.And.Property("HasValues").True, "bookings.supportingEventPayload with valid structure is required in bookingData.json");
        Assert.That(data.Bookings.CreatePayload, Is.Not.Null.And.Property("HasValues").True, "bookings.createPayload with valid structure is required in bookingData.json");
        Assert.That(data.Bookings.InvalidCreatePayload, Is.Not.Null.And.Property("HasValues").True, "bookings.invalidCreatePayload with valid structure is required in bookingData.json");
        
        Assert.That(data.Assertions, Is.Not.Null, "data.Assertions cannot be null in bookingData.json.");
        Assert.That(data.Assertions.CreatedBookingIdJsonPath, Is.Not.Null.And.Not.Empty, "assertions.createdBookingIdJsonPath is required in bookingData.json");
        Assert.That(data.Assertions.BookingReferenceJsonPath, Is.Not.Null.And.Not.Empty, "assertions.bookingReferenceJsonPath is required in bookingData.json");
        Assert.That(data.Assertions.SupportingEventIdJsonPath, Is.Not.Null.And.Not.Empty, "assertions.supportingEventIdJsonPath is required in bookingData.json");
        Assert.That(data.Queries, Is.Not.Null, "data.Queries cannot be null in bookingData.json.");

        return data;
    }

    private LoginDataModel LoadLoginData()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "loginData.json");
        Assert.That(File.Exists(path), Is.True, $"loginData file should exist at {path}. Ensure the file is present and copied to the output directory.");

        var data = JsonDataProvider.Read<LoginDataModel>(path);
        Assert.That(data, Is.Not.Null, "loginData.json must be valid JSON and deserializable to LoginDataModel.");
        Assert.That(data!.Roles, Is.Not.Null, "roles section is required in loginData.json.");
        Assert.That(data.Roles.Count, Is.GreaterThan(0),
            "At least one role must be configured in loginData.json.");
        
        Assert.That(data.ApiAuth, Is.Not.Null, "apiAuth section is required in loginData.json for API authentication configuration.");
        Assert.That(data.WrongPasswordScenario, Is.Not.Null, "wrongPasswordScenario section is required in loginData.json for negative test scenarios.");

        return data;
    }

    protected async Task<TokenState?> LoginAsync(
        string email,
        string password,
        string tokenScenario = "valid",
        bool tokenState = true,
        CancellationToken cancellationToken = default)
    {
        Assert.That(SharedAuthClient, Is.Not.Null, "SharedAuthClient should be initialized before calling LoginAsync.");

        // Fast path: reuse the valid suite-level token without an HTTP call for positive flows.
        // NUnit runs [SetUp] and the test body in separate ExecutionContext instances, so AsyncLocal
        // values written in [SetUp] are NOT visible in the test body. Calling this method from the
        // test body re-injects the suite token into the current execution context without network cost.
        // Only applies when the caller supplies the same credentials used for the suite login -
        // if different credentials are provided the request must go to the network so the server
        // can reject them (e.g. wrong-password negative tests).
        if (tokenState
            && string.Equals(tokenScenario, "valid", StringComparison.OrdinalIgnoreCase)
            && TryGetCachedSuiteToken(email, password, out var cachedToken)
            && cachedToken?.IsValid == true)
        {
            ApiSessionContext.Current.SetToken(cachedToken);
            ApiSessionContext.Current.StoreCredentials(email, password);
            return cachedToken;
        }

        var configured = await SharedAuthClient
            .LoginAsync(email, password, tokenScenario, tokenState, cancellationToken)
            .ConfigureAwait(false);

        // Rebind token into the current test execution context to avoid AsyncLocal context drift.
        if (configured is null)
        {
            ApiSessionContext.Current.ClearToken();
            ApiSessionContext.Current.ClearStoredCredentials();
        }
        else
        {
            ApiSessionContext.Current.SetToken(configured);
            ApiSessionContext.Current.StoreCredentials(email, password);
        }

        return configured;
    }

    /// <summary>
    /// Ensures a valid token is bound to the current test's async execution context.
    /// Call at the start of any positive-flow test body that makes authenticated API calls.
    /// Reuses the suite-level token without HTTP if still valid; falls back to a fresh login otherwise.
    /// </summary>
    protected async Task EnsureValidTokenAsync()
    {
        await EnsureValidTokenAsync(GetCurrentTestRole());
    }

    protected async Task EnsureValidTokenAsync(string role)
    {
        var roleCredentials = ResolveRoleCredentials(role);
        await LoginAsync(
            roleCredentials.Email,
            roleCredentials.Password,
            tokenScenario: "valid",
            tokenState: true);
    }

    protected Task<TokenState?> LoginAsRoleAsync(
        string role,
        string tokenScenario = "valid",
        bool tokenState = true,
        CancellationToken cancellationToken = default)
    {
        var credentials = ResolveRoleCredentials(role);
        return LoginAsync(credentials.Email, credentials.Password, tokenScenario, tokenState, cancellationToken);
    }

    protected Task<TokenState?> LoginAsCurrentRoleAsync(
        string tokenScenario = "valid",
        bool tokenState = true,
        CancellationToken cancellationToken = default)
    {
        return LoginAsRoleAsync(GetCurrentTestRole(), tokenScenario, tokenState, cancellationToken);
    }

    /// <summary>
    /// Resolves credentials for the requested role using provider/resolver strategy.
    /// </summary>
    /// <param name="role">Role key to resolve (for example: admin, user, organizer, viewer).</param>
    /// <returns>Tuple of email and password for the role.</returns>
    protected (string Email, string Password) ResolveRoleCredentials(string? role = null)
    {
        var resolvedRole = role ?? GetCurrentTestRole();
        var credentials = RoleCredentialResolver.Resolve(resolvedRole, RoleCredentials);
        return (credentials.Email, credentials.Password);
    }

    private static RoleCredentialProvider BuildRoleCredentialProvider(LoginDataModel data)
    {
        var roles = data.Roles
            .ToDictionary(
                kvp => kvp.Key,
                kvp => new RoleCredentials(kvp.Value.Email, kvp.Value.Password),
                StringComparer.OrdinalIgnoreCase);

        return RoleCredentialProvider.Create(roles);
    }

    /// <summary>
    /// Binds an already-issued suite token into the current test execution context without
    /// performing any network login. Returns false if the suite token is missing or expired.
    /// </summary>
    protected bool TryBindSuiteTokenToCurrentContext(string? role = null)
    {
        var resolvedRole = role ?? GetCurrentTestRole();
        if (!_suiteTokens.TryGetValue(resolvedRole, out var suiteToken) || suiteToken is null || !suiteToken.IsValid)
        {
            return false;
        }

        var credentials = _suiteCredentialsByRole[resolvedRole];
        ApiSessionContext.Current.SetToken(suiteToken);
        ApiSessionContext.Current.StoreCredentials(credentials.Email, credentials.Password);
        return true;
    }

    /// <summary>
    /// One-line helper for positive tests: bind suite token to current context or mark test inconclusive.
    /// Keeps test bodies clean while preserving suite-level auth intent.
    /// </summary>
    protected void EnsurePositiveAuthContextOrInconclusive(string flowName, string? role = null)
    {
        var resolvedRole = role ?? GetCurrentTestRole();
        if (TryBindSuiteTokenToCurrentContext(resolvedRole))
        {
            return;
        }

        Assert.Inconclusive($"No suite token available for positive {flowName} flow using role '{resolvedRole}'.");
    }

    private async Task EnsureSuiteTokenAsync(string? role = null)
    {
        var resolvedRole = role ?? GetCurrentTestRole();
        if (!_suiteCredentialsByRole.ContainsKey(resolvedRole))
        {
            await PrimeRoleSuiteTokenAsync(resolvedRole).ConfigureAwait(false);
        }

        if (!_suiteTokens.TryGetValue(resolvedRole, out var suiteToken) || suiteToken is null)
        {
            ApiSessionContext.Current.ClearToken();
            ApiSessionContext.Current.ClearStoredCredentials();
            return;
        }

        var roleCredentials = _suiteCredentialsByRole[resolvedRole];

        if (suiteToken.IsValid)
        {
            ApiSessionContext.Current.SetToken(suiteToken);
            ApiSessionContext.Current.StoreCredentials(roleCredentials.Email, roleCredentials.Password);
            return;
        }

        // Token expired between tests. Refresh under lock so only one test triggers re-auth.
        await _suiteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_suiteTokens.TryGetValue(resolvedRole, out suiteToken) && suiteToken?.IsValid == true)
            {
                ApiSessionContext.Current.SetToken(suiteToken);
                ApiSessionContext.Current.StoreCredentials(roleCredentials.Email, roleCredentials.Password);
                return;
            }

            Logger.Information("Suite token for role {Role} expired. Re-authenticating before next test...", resolvedRole);
            try
            {
                suiteToken = await SharedAuthClient.LoginAsync(
                    roleCredentials.Email,
                    roleCredentials.Password,
                    tokenScenario: "valid",
                    tokenState: true).ConfigureAwait(false);

                _suiteTokens[resolvedRole] = suiteToken;

                if (suiteToken is not null)
                {
                    Logger.Information("Suite token for role {Role} refreshed. Valid until {ExpiresAt}.", resolvedRole, suiteToken.ExpiresAt);
                    ApiSessionContext.Current.SetToken(suiteToken);
                    ApiSessionContext.Current.StoreCredentials(roleCredentials.Email, roleCredentials.Password);
                }
            }
            catch (InvalidOperationException ex)
            {
                Logger.Warning("Suite token refresh failed for role {Role}: {Message}", resolvedRole, ex.Message);
                _suiteTokens[resolvedRole] = null;
                ApiSessionContext.Current.ClearToken();
                ApiSessionContext.Current.ClearStoredCredentials();
            }
        }
        finally
        {
            _suiteLock.Release();
        }
    }

    private async Task PrimeRoleSuiteTokenAsync(string role)
    {
        try
        {
            var suiteRoleCredentials = ResolveRoleCredentials(role);
            _suiteCredentialsByRole[role] = (suiteRoleCredentials.Email, suiteRoleCredentials.Password);
            _suiteTokens[role] = await SharedAuthClient.LoginAsync(
                suiteRoleCredentials.Email,
                suiteRoleCredentials.Password,
                tokenScenario: "valid",
                tokenState: true);

            Logger.Information("Suite login succeeded for role {Role}. Token valid until {ExpiresAt}.", role, _suiteTokens[role]?.ExpiresAt);
        }
        catch (InvalidOperationException ex)
        {
            Logger.Warning("Suite login failed for role {Role}. Positive tests for that role will proceed without a pre-loaded token. Error: {Message}", role, ex.Message);
            _suiteTokens[role] = null;
        }
    }

    private bool TryGetCachedSuiteToken(string email, string password, out TokenState? token)
    {
        foreach (var entry in _suiteCredentialsByRole)
        {
            if (!string.Equals(entry.Value.Email, email, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(entry.Value.Password, password, StringComparison.Ordinal))
            {
                continue;
            }

            token = _suiteTokens.GetValueOrDefault(entry.Key);
            return token is not null;
        }

        token = null;
        return false;
    }

    private static EndpointData BuildEndpointData(
        EndpointData.AuthEndpointData authEndpoints,
        EndpointData.EventEndpointData eventEndpoints,
        EndpointData.BookingEndpointData bookingEndpoints)
    {
        ArgumentNullException.ThrowIfNull(authEndpoints);
        ArgumentNullException.ThrowIfNull(eventEndpoints);
        ArgumentNullException.ThrowIfNull(bookingEndpoints);

        return new EndpointData
        {
            Auth = authEndpoints,
            Events = eventEndpoints,
            Bookings = bookingEndpoints
        };
    }

    private static void ValidateAuthEndpoints(EndpointData.AuthEndpointData endpoints, string sourceFile)
    {
        Assert.That(endpoints.Login, Is.Not.Null.And.Not.Empty, $"apiAuth.endpoints.login is required in {sourceFile}");
        Assert.That(endpoints.Me, Is.Not.Null.And.Not.Empty, $"apiAuth.endpoints.me is required in {sourceFile}");
    }

    private static void ValidateEventEndpoints(EndpointData.EventEndpointData endpoints, string sourceFile)
    {
        Assert.That(endpoints.List, Is.Not.Null.And.Not.Empty, $"endpoints.list is required in {sourceFile}");
        Assert.That(endpoints.Create, Is.Not.Null.And.Not.Empty, $"endpoints.create is required in {sourceFile}");
        Assert.That(endpoints.GetById, Is.Not.Null.And.Not.Empty, $"endpoints.getById is required in {sourceFile}");
        Assert.That(endpoints.UpdateById, Is.Not.Null.And.Not.Empty, $"endpoints.updateById is required in {sourceFile}");
        Assert.That(endpoints.DeleteById, Is.Not.Null.And.Not.Empty, $"endpoints.deleteById is required in {sourceFile}");
    }

    private static void ValidateBookingEndpoints(EndpointData.BookingEndpointData endpoints, string sourceFile)
    {
        Assert.That(endpoints.List, Is.Not.Null.And.Not.Empty, $"endpoints.list is required in {sourceFile}");
        Assert.That(endpoints.Create, Is.Not.Null.And.Not.Empty, $"endpoints.create is required in {sourceFile}");
        Assert.That(endpoints.GetById, Is.Not.Null.And.Not.Empty, $"endpoints.getById is required in {sourceFile}");
        Assert.That(endpoints.GetByReference, Is.Not.Null.And.Not.Empty, $"endpoints.getByReference is required in {sourceFile}");
        Assert.That(endpoints.CancelById, Is.Not.Null.And.Not.Empty, $"endpoints.cancelById is required in {sourceFile}");
    }

    protected JObject BuildPayload(JObject template, IDictionary<string, JToken>? variables = null)
    {
        Assert.That(template, Is.Not.Null, "Payload template cannot be null. Ensure the payload template is loaded from test data.");
        Assert.That(template.HasValues, Is.True, "Payload template must have values. Ensure the template JSON object is not empty.");
        
        var payload = (JObject)template.DeepClone();
        Assert.That(payload, Is.Not.Null, "Failed to clone payload template.");
        
        ApplyTemplateValues(payload, variables ?? new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase));
        return payload;
    }

    protected int ExtractRequiredInt(string responseBody, string jsonPath, string missingMessage)
    {
        Assert.That(responseBody, Is.Not.Null.And.Not.Empty, "Response body cannot be null or empty when extracting integer values.");
        Assert.That(jsonPath, Is.Not.Null.And.Not.Empty, "JSONPath cannot be null or empty when extracting values.");
        Assert.That(missingMessage, Is.Not.Null.And.Not.Empty, "Error message cannot be null or empty.");
        
        var token = ExtractRequiredToken(responseBody, jsonPath, missingMessage);
        Assert.That(token.Type, Is.EqualTo(JTokenType.Integer).Or.EqualTo(JTokenType.String), 
            $"Token at JSONPath '{jsonPath}' should be an integer or numeric string, but found {token.Type}.");
        
        try
        {
            return token.Value<int>();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to convert token at JSONPath '{jsonPath}' to integer. Value: {token}. {missingMessage}", ex);
        }
    }

    protected string ExtractRequiredString(string responseBody, string jsonPath, string missingMessage)
    {
        Assert.That(responseBody, Is.Not.Null.And.Not.Empty, "Response body cannot be null or empty when extracting string values.");
        Assert.That(jsonPath, Is.Not.Null.And.Not.Empty, "JSONPath cannot be null or empty when extracting values.");
        Assert.That(missingMessage, Is.Not.Null.And.Not.Empty, "Error message cannot be null or empty.");
        
        var token = ExtractRequiredToken(responseBody, jsonPath, missingMessage);
        
        try
        {
            var value = token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
            return string.IsNullOrWhiteSpace(value)
                ? throw new InvalidOperationException($"{missingMessage} Extracted value at JSONPath '{jsonPath}' was null or empty.")
                : value;
        }
        catch (Exception ex) when (!(ex is InvalidOperationException))
        {
            throw new InvalidOperationException($"Failed to extract string value from JSONPath '{jsonPath}'. {missingMessage}", ex);
        }
    }

    private static JToken ExtractRequiredToken(string responseBody, string jsonPath, string missingMessage)
    {
        Assert.That(responseBody, Is.Not.Null.And.Not.Empty, "Response body cannot be null or empty when parsing JSON.");
        Assert.That(jsonPath, Is.Not.Null.And.Not.Empty, "JSONPath cannot be null or empty.");
        
        JObject root;
        try
        {
            root = JObject.Parse(responseBody);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to parse response body as JSON. Response: {responseBody.Substring(0, Math.Min(200, responseBody.Length))}...", ex);
        }
        
        var token = root.SelectToken(jsonPath);
        return token ?? throw new InvalidOperationException($"{missingMessage} JSONPath '{jsonPath}' not found in response.");
    }

    private static void ApplyTemplateValues(JToken token, IDictionary<string, JToken> variables)
    {
        switch (token)
        {
            case JObject obj:
                foreach (var property in obj.Properties().ToList())
                {
                    ApplyTemplateValues(property.Value, variables);
                }
                break;

            case JArray array:
                foreach (var item in array)
                {
                    ApplyTemplateValues(item, variables);
                }
                break;

            case JValue value when value.Type == JTokenType.String && value.Value is string text:
                value.Replace(ResolveTemplateValue(text, variables));
                break;
        }
    }

    private static JToken ResolveTemplateValue(string template, IDictionary<string, JToken> variables)
    {
        var fullPlaceholderMatch = Regex.Match(template, @"^\{([^{}]+)\}$");
        if (fullPlaceholderMatch.Success)
        {
            return ResolvePlaceholder(fullPlaceholderMatch.Groups[1].Value, variables);
        }

        var resolved = Regex.Replace(template, "\\{([^{}]+)\\}", match =>
        {
            var token = ResolvePlaceholder(match.Groups[1].Value, variables);
            return token.Type == JTokenType.String ? token.Value<string>()! : token.ToString();
        });

        return new JValue(resolved);
    }

    private static JToken ResolvePlaceholder(string name, IDictionary<string, JToken> variables)
    {
        if (variables.TryGetValue(name, out var value))
        {
            return value.DeepClone();
        }

        if (string.Equals(name, "timestamp", StringComparison.OrdinalIgnoreCase))
        {
            return new JValue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
        }

        var dateMatch = Regex.Match(name, @"^utcNowPlusDays:(-?\d+):(.+)$");
        if (dateMatch.Success)
        {
            var dayOffset = int.Parse(dateMatch.Groups[1].Value);
            var format = dateMatch.Groups[2].Value;
            return new JValue(DateTimeOffset.UtcNow.AddDays(dayOffset).ToString(format));
        }

        throw new InvalidOperationException($"Unknown payload template placeholder '{{{name}}}'.");
    }

    [SetUp]
    public async Task SetUp()
    {
        Assert.That(TestContext.CurrentContext, Is.Not.Null, "TestContext must be available in SetUp.");
        Assert.That(TestContext.CurrentContext.Test, Is.Not.Null, "TestContext.Test must not be null.");
        Assert.That(TestContext.CurrentContext.Test.Name, Is.Not.Null.And.Not.Empty, "Test name must be available.");
        Assert.That(AuthApi, Is.Not.Null, "AuthApi page object should be initialized from OneTimeSetUp.");
        Assert.That(SharedApiClient, Is.Not.Null, "SharedApiClient should be initialized from OneTimeSetUp.");
        Assert.That(LoginData, Is.Not.Null, "LoginData should be initialized from OneTimeSetUp.");
        
        _testStartedAt = DateTimeOffset.Now;
        _executionTimer.Restart();
        _priority = GetCurrentPriorityLevel()?.ToString() ?? "Unspecified";
        _suiteName = GetCurrentSuiteName();

        var executionLogger = TestLogger.CreateExecutionLogger("API", _suiteName, TestContext.CurrentContext.Test.Name);
        Assert.That(executionLogger, Is.Not.Null, "ExecutionLogger initialization failed.");
        
        Logger = executionLogger.ForContext<APITestBase>();
        Assert.That(Logger, Is.Not.Null, "Logger context creation failed.");

        _executionLoggerHandle = executionLogger as IDisposable;

        RebindApiDependenciesWithExecutionLogger();

        Logger.Information("[API] Starting test {TestName}", TestContext.CurrentContext.Test.Name);
        RuntimeContext.TestType = "API";
        RuntimeContext.BrowserName = "N/A";
        
        try
        {
            BeginReportTest();
        }
        catch (Exception ex)
        {
            Logger.Warning("Failed to begin report test: {Message}", ex.Message);
        }
        
        ReportHelper.BeginTest(TestContext.CurrentContext.Test.Name);

        // Load the suite token into this test's AsyncLocal context.
        // Negative tests override this immediately after [SetUp] via LoginAsync().
        await EnsureSuiteTokenAsync();
    }

    [TearDown]
    public void TearDown()
    {
        var outcome = TestContext.CurrentContext.Result.Outcome.Status.ToString();
        var errorMessage = TestContext.CurrentContext.Result.Message;
        _executionTimer.Stop();
        var finishedAt = DateTimeOffset.Now;
        
        // Attach any collected error information to the report
        CompleteReportTest();
        Logger.Information("[API] Completing test {TestName} with status {Status}", TestContext.CurrentContext.Test.Name, outcome);

        var fullClassName = TestContext.CurrentContext.Test.ClassName ?? string.Empty;
        var shortClassName = fullClassName.Contains('.') ? fullClassName[(fullClassName.LastIndexOf('.') + 1)..] : fullClassName;

        ReportHelper.RecordTestResult(
            TestContext.CurrentContext.Test.Name,
            shortClassName,
            _suiteName,
            outcome,
            _executionTimer.Elapsed,
            "N/A",
            "API",
            _priority,
            _testStartedAt,
            finishedAt,
            errorMessage,
            null);

        // Report is generated once per test run in global teardown.
        if (TestContext.CurrentContext.Result.Outcome.Status == TestStatus.Failed)
        {
            Logger.Debug("[API] Test {TestName} completed with failure status", TestContext.CurrentContext.Test.Name);
        }

        Logger.Information("[API] Test {TestName} finished in {DurationMs} ms",
            TestContext.CurrentContext.Test.Name,
            _executionTimer.Elapsed.TotalMilliseconds);

        _executionLoggerHandle?.Dispose();
        _executionLoggerHandle = null;
    }

}

public sealed class LoginDataModel
{
    public Dictionary<string, CredentialsModel> Roles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public CredentialsModel WrongPasswordScenario { get; set; } = new();
    public ApiAuthData ApiAuth { get; set; } = new();

    public sealed class CredentialsModel
    {
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }
}

public sealed class ApiAuthData
{
    public EndpointData.AuthEndpointData Endpoints { get; set; } = new();
    public ApiAuthAssertionData Assertions { get; set; } = new();

    public sealed class ApiAuthAssertionData
    {
        public string TokenJsonPath { get; set; } = string.Empty;
        public string CurrentUserEmailJsonPath { get; set; } = string.Empty;
    }
}

public sealed class EventApiDataModel
{
    public EndpointData.EventEndpointData Endpoints { get; set; } = new();
    public ApiSuiteData.EventData Events { get; set; } = new();
    public ApiSuiteData.QueryData.EventQueryData Queries { get; set; } = new();
    public ApiSuiteData.AssertionData.EventAssertionData Assertions { get; set; } = new();
}

public sealed class BookingApiDataModel
{
    public EndpointData.BookingEndpointData Endpoints { get; set; } = new();
    public ApiSuiteData.BookingData Bookings { get; set; } = new();
    public ApiSuiteData.QueryData.BookingQueryData Queries { get; set; } = new();
    public ApiSuiteData.AssertionData.BookingAssertionData Assertions { get; set; } = new();
}

public sealed class ApiSuiteData
{
    public EndpointData Endpoints { get; set; } = new();
    public EventData Events { get; set; } = new();
    public BookingData Bookings { get; set; } = new();
    public QueryData Queries { get; set; } = new();
    public AssertionData Assertions { get; set; } = new();

    // EndpointData and nested endpoint classes are now in Framework.Contracts

    public sealed class EventData
    {
        public JObject CreatePayload { get; set; } = new();
        public JObject UpdatePayload { get; set; } = new();
        public JObject InvalidCreatePayload { get; set; } = new();
    }

    public sealed class BookingData
    {
        public JObject SupportingEventPayload { get; set; } = new();
        public JObject CreatePayload { get; set; } = new();
        public JObject InvalidCreatePayload { get; set; } = new();
    }

    public sealed class QueryData
    {
        public EventQueryData Events { get; set; } = new();
        public BookingQueryData Bookings { get; set; } = new();

        public sealed class EventQueryData
        {
            public int Page { get; set; }
            public int Limit { get; set; }
            public string Category { get; set; } = string.Empty;
            public string City { get; set; } = string.Empty;
            public string Search { get; set; } = string.Empty;
        }

        public sealed class BookingQueryData
        {
            public int Page { get; set; }
            public int Limit { get; set; }
            public string Status { get; set; } = string.Empty;
        }
    }

    public sealed class AssertionData
    {
        public AuthAssertionData Auth { get; set; } = new();
        public EventAssertionData Events { get; set; } = new();
        public BookingAssertionData Bookings { get; set; } = new();

        public sealed class AuthAssertionData
        {
            public string TokenJsonPath { get; set; } = string.Empty;
            public string CurrentUserEmailJsonPath { get; set; } = string.Empty;
        }

        public sealed class EventAssertionData
        {
            public string PaginationField { get; set; } = string.Empty;
            public string ValidationErrorField { get; set; } = string.Empty;
            public string CreatedEventIdJsonPath { get; set; } = string.Empty;
        }

        public sealed class BookingAssertionData
        {
            public string PaginationField { get; set; } = string.Empty;
            public string ValidationErrorField { get; set; } = string.Empty;
            public string CreatedBookingIdJsonPath { get; set; } = string.Empty;
            public string BookingReferenceJsonPath { get; set; } = string.Empty;
            public string SupportingEventIdJsonPath { get; set; } = string.Empty;
        }
    }
}
