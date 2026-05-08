using Newtonsoft.Json.Linq;

namespace Framework.API;

/// <summary>
/// Basic auth/session utility for test-driven token scenarios.
/// Tests choose the token scenario explicitly: valid, expired, invalid, or missing.
/// </summary>
public sealed class AuthClient
{
    private readonly APIClient _httpClient;
    private readonly Serilog.ILogger _logger;
    private readonly string _loginEndpoint;
    private readonly string _tokenJsonPath;
    private readonly int _defaultTokenTtlSeconds;

    public AuthClient(
        APIClient httpClient,
        Serilog.ILogger logger,
        string loginEndpoint,
        string tokenJsonPath,
        int defaultTokenTtlSeconds = 3600)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loginEndpoint = !string.IsNullOrWhiteSpace(loginEndpoint)
            ? loginEndpoint
            : throw new ArgumentException("Login endpoint cannot be null or empty.", nameof(loginEndpoint));
        _tokenJsonPath = !string.IsNullOrWhiteSpace(tokenJsonPath)
            ? tokenJsonPath
            : throw new ArgumentException("Token JSONPath cannot be null or empty.", nameof(tokenJsonPath));
        _defaultTokenTtlSeconds = defaultTokenTtlSeconds > 0
            ? defaultTokenTtlSeconds
            : throw new ArgumentException("Token TTL must be greater than 0.", nameof(defaultTokenTtlSeconds));
    }

    /// <summary>
    /// Configures auth session based on test intent.
    /// Example: LoginAsync(email, password, "valid", true)
    /// Example: LoginAsync(email, password, "invalid", false)
    /// Example: LoginAsync(email, password, "expired", false)
    /// </summary>
    public async Task<TokenState?> LoginAsync(
        string email,
        string password,
        string tokenScenario = "valid",
        bool tokenState = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException("Email cannot be null or empty.", nameof(email));
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("Password cannot be null or empty.", nameof(password));
        }

        if (tokenState && !string.Equals(tokenScenario, "valid", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Invalid test setup: tokenState=true requires tokenScenario='valid'. Received '{tokenScenario}'.");
        }

        var scenario = ParseScenario(tokenScenario);
        var response = await ExecuteLoginRequestAsync(email, password, cancellationToken).ConfigureAwait(false);
        var responseBody = response.Content is null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            if (tokenState && scenario == AuthTokenScenario.Valid)
            {
                throw new InvalidOperationException(
                    $"Invalid test setup: tokenState=true with valid token requested, but login failed. " +
                    $"Credentials may be incorrect. Status {(int)response.StatusCode} {response.ReasonPhrase}. Response: {responseBody}");
            }

            if (scenario == AuthTokenScenario.Missing)
            {
                ApiSessionContext.Current.ClearToken();
                ApiSessionContext.Current.ClearStoredCredentials();
                return null;
            }

            // For negative invalid/expired scenarios with wrong creds, create synthetic unusable token.
            var synthetic = BuildSyntheticToken(scenario);
            ApiSessionContext.Current.SetToken(synthetic);
            ApiSessionContext.Current.StoreCredentials(email, password);
            return synthetic;
        }

        var extracted = ExtractTokenFromResponse(responseBody);
        var finalToken = scenario switch
        {
            AuthTokenScenario.Valid => extracted,
            AuthTokenScenario.Expired => extracted with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5), AllowRefresh = false },
            AuthTokenScenario.Invalid => extracted with { AccessToken = "invalid-token-for-negative-scenario", AllowRefresh = false },
            AuthTokenScenario.Missing => null,
            _ => throw new InvalidOperationException($"Unsupported token scenario: {scenario}")
        };

        if (finalToken is null)
        {
            ApiSessionContext.Current.ClearToken();
            ApiSessionContext.Current.ClearStoredCredentials();
            return null;
        }

        ApiSessionContext.Current.SetToken(finalToken);
        ApiSessionContext.Current.StoreCredentials(email, password);

        if (tokenState && !finalToken.IsValid)
        {
            throw new InvalidOperationException(
                "Invalid test setup: tokenState=true but generated token is not valid (expired/invalid). " +
                "Use tokenScenario='valid' for positive flows.");
        }

        return finalToken;
    }

    public bool GetCurrentTokenState()
    {
        return ApiSessionContext.Current.CurrentToken?.IsValid == true;
    }

    public TokenState? GetCurrentTokenDetails()
    {
        return ApiSessionContext.Current.CurrentToken;
    }

    public void ValidateTokenExists(bool shouldExist, string expectedState)
    {
        var token = ApiSessionContext.Current.CurrentToken;
        var actual = ClassifyState(token);
        var expected = NormalizeExpectedState(expectedState);

        if (shouldExist && token is null)
        {
            throw new InvalidOperationException("Expected token to exist, but no token is present.");
        }

        if (!shouldExist && token is null)
        {
            return;
        }

        if (expected != actual)
        {
            throw new InvalidOperationException($"Expected token state '{expected}', but actual state is '{actual}'.");
        }
    }

    public async Task<HttpResponseMessage> ExecuteLoginRequestAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        var payload = new { email, password };
        var request = new APIRequestBuilder()
            .WithMethod(HttpMethod.Post)
            .WithEndpoint(_loginEndpoint)
            .WithJsonBody(payload)
            .Build();

        using (request)
        {
            return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    public TokenState ExtractTokenFromResponse(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            throw new InvalidOperationException("Response body cannot be null or empty when extracting token.");
        }

        var response = JObject.Parse(responseBody);
        var tokenValue = response.SelectToken(_tokenJsonPath);
        if (tokenValue is null)
        {
            throw new InvalidOperationException($"Token not found at JSONPath '{_tokenJsonPath}'.");
        }

        var token = tokenValue.Value<string>();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Extracted token is null or empty.");
        }

        var expiresInToken = response.SelectToken("$.expiresIn") ?? response.SelectToken("$.expires_in");
        var ttlSeconds = _defaultTokenTtlSeconds;
        if (expiresInToken is not null && int.TryParse(expiresInToken.ToString(), out var serverTtl) && serverTtl > 0)
        {
            ttlSeconds = serverTtl;
        }

        return new TokenState(token, DateTimeOffset.UtcNow.AddSeconds(ttlSeconds));
    }

    public static TokenState BuildSyntheticToken(AuthTokenScenario scenario)
    {
        return scenario switch
        {
            AuthTokenScenario.Invalid => new TokenState("invalid-token-for-negative-scenario", DateTimeOffset.UtcNow.AddHours(1), AllowRefresh: false),
            AuthTokenScenario.Expired => new TokenState("expired-token-for-negative-scenario", DateTimeOffset.UtcNow.AddHours(-1), AllowRefresh: false),
            _ => throw new InvalidOperationException($"Synthetic token generation not supported for scenario {scenario}")
        };
    }

    public static string ClassifyState(TokenState? token)
    {
        if (token is null)
        {
            return "missing";
        }

        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            return "invalid";
        }

        return token.IsValid ? "valid" : "expired";
    }

    public static string NormalizeExpectedState(string expectedState)
    {
        var normalized = (expectedState ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "valid" => "valid",
            "expired" => "expired",
            "invalid" => "invalid",
            "missing" => "missing",
            _ => throw new ArgumentException("Supported expected states are: valid, expired, invalid, missing.", nameof(expectedState))
        };
    }

    public static AuthTokenScenario ParseScenario(string tokenScenario)
    {
        var normalized = (tokenScenario ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "valid" => AuthTokenScenario.Valid,
            "expired" => AuthTokenScenario.Expired,
            "invalid" => AuthTokenScenario.Invalid,
            "missing" => AuthTokenScenario.Missing,
            _ => throw new ArgumentException("Supported token scenarios are: valid, expired, invalid, missing.", nameof(tokenScenario))
        };
    }
}

public enum AuthTokenScenario
{
    Valid,
    Expired,
    Invalid,
    Missing
}
