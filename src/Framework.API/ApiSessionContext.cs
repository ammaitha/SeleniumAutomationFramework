using Newtonsoft.Json.Linq;

namespace Framework.API;

/// <summary>
/// Immutable auth token state stored at a point in time.
/// </summary>
public sealed record TokenState(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string? RefreshToken = null,
    bool AllowRefresh = true)
{
    /// <summary>
    /// Returns true if the token is expired or will expire within 30 seconds (grace period).
    /// </summary>
    public bool IsExpiredOrExpiring => DateTimeOffset.UtcNow.AddSeconds(30) >= ExpiresAt;

    /// <summary>
    /// Returns time remaining before token expires.
    /// </summary>
    public TimeSpan TimeRemaining => ExpiresAt - DateTimeOffset.UtcNow;

    /// <summary>
    /// Returns true if the token is still valid (not expired and not expiring soon).
    /// </summary>
    public bool IsValid => !IsExpiredOrExpiring;
}

/// <summary>
/// In-memory, AsyncLocal-scoped API session context that stores authentication tokens.
/// Provides thread-safe access to auth state across async call chains in a single test execution.
/// 
/// Thread safety:
/// - AsyncLocal&lt;T&gt; automatically isolates context per async execution path
/// - Internal SemaphoreSlim protects token refresh operations to prevent concurrent refresh calls
/// - Write operations are atomic (TokenState is immutable; reference replacement is atomic in .NET)
/// 
/// Lifetime:
/// - Created once per test execution/session
/// - Cleared in OneTimeTearDown
/// - Does NOT persist across test runs, environment variables, or disk
/// </summary>
public sealed class ApiSessionContext
{
    private static readonly AsyncLocal<ApiSessionContext?> _currentContext = new();
    private TokenState? _tokenState;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    
    /// <summary>
    /// Stores the last used email for re-authentication on token expiry.
    /// WARNING: This stores credentials in memory. Only use for test scenarios.
    /// </summary>
    private string? _lastUsedEmail;

    /// <summary>
    /// Stores the last used password for re-authentication on token expiry.
    /// WARNING: This stores credentials in memory. Only use for test scenarios.
    /// </summary>
    private string? _lastUsedPassword;

    /// <summary>
    /// Gets or creates the session context for the current async execution context.
    /// </summary>
    public static ApiSessionContext Current
    {
        get
        {
            _currentContext.Value ??= new ApiSessionContext();
            return _currentContext.Value;
        }
    }

    /// <summary>
    /// Gets the current token state, or null if no authentication has been performed.
    /// </summary>
    public TokenState? CurrentToken => _tokenState;

    /// <summary>
    /// Gets the current valid access token string, or null if not authenticated or expired.
    /// Note: Returns null if token is within 30-second grace period of expiration (used for auto-refresh).
    /// For immediate access after authentication, use <see cref="CurrentAccessToken"/> instead.
    /// </summary>
    public string? AccessToken
    {
        get
        {
            var token = _tokenState;
            return token?.IsValid == true ? token.AccessToken : null;
        }
    }

    /// <summary>
    /// Gets the current access token string without validity checks, or null if not authenticated.
    /// This returns the raw token regardless of expiration status.
    /// Useful for diagnostic purposes and immediate post-authentication verification.
    /// </summary>
    public string? CurrentAccessToken
    {
        get
        {
            return _tokenState?.AccessToken;
        }
    }

    /// <summary>
    /// Stores a new token state in the session context.
    /// Thread-safe: atomic reference replacement.
    /// </summary>
    public void SetToken(TokenState tokenState)
    {
        if (tokenState is null)
        {
            throw new ArgumentNullException(nameof(tokenState), "TokenState cannot be null.");
        }

        _tokenState = tokenState;
    }

    /// <summary>
    /// Clears all authentication state from the session context.
    /// Typically called in OneTimeTearDown.
    /// </summary>
    public void ClearToken()
    {
        _tokenState = null;
    }

    /// <summary>
    /// Stores the credentials used for authentication (for later re-authentication if token expires).
    /// WARNING: This stores credentials in memory. Only use for test scenarios.
    /// </summary>
    public void StoreCredentials(string email, string password)
    {
        _lastUsedEmail = email;
        _lastUsedPassword = password;
    }

    /// <summary>
    /// Retrieves the last stored credentials for token re-freshment.
    /// Returns null if no credentials have been stored.
    /// </summary>
    public (string? Email, string? Password) GetStoredCredentials()
    {
        return (_lastUsedEmail, _lastUsedPassword);
    }

    /// <summary>
    /// Clears stored credentials from memory.
    /// </summary>
    public void ClearStoredCredentials()
    {
        _lastUsedEmail = null;
        _lastUsedPassword = null;
    }

    /// <summary>
    /// Acquires an exclusive lock for performing token refresh operations.
    /// Ensures only one refresh operation runs concurrently for this session.
    /// </summary>
    public async ValueTask<IDisposable> AcquireRefreshLockAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new RefreshLockReleaser(_refreshLock);
    }

    /// <summary>
    /// Clears the current async local context (for test isolation or global cleanup).
    /// </summary>
    public static void ClearCurrentContext()
    {
        var context = _currentContext.Value;
        context?.ClearStoredCredentials();
        _currentContext.Value = null;
    }

    /// <summary>
    /// Fetches the current user payload from an authenticated identity endpoint and returns it as compact JSON.
    /// This is useful when bootstrapping UI session storage from API-authenticated context.
    /// </summary>
    public static string FetchCurrentUserJson(
        APIClient apiClient,
        string meEndpoint,
        string authToken,
        string primaryPath = "user",
        string fallbackPath = "data")
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(meEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(authToken);

        var meRequest = new APIRequestBuilder()
            .WithMethod(HttpMethod.Get)
            .WithEndpoint(meEndpoint)
            .WithBearerToken(authToken)
            .Build();

        var meHttpResponse = apiClient.SendAsync(meRequest).GetAwaiter().GetResult();
        var meBody = meHttpResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        var meJson = JObject.Parse(meBody);

        return (meJson.SelectToken(primaryPath) ?? meJson.SelectToken(fallbackPath) ?? new JObject())
            .ToString(Newtonsoft.Json.Formatting.None);
    }

    /// <summary>
    /// Helper to properly release the refresh lock.
    /// </summary>
    private sealed class RefreshLockReleaser : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed;

        public RefreshLockReleaser(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _semaphore.Release();
                _disposed = true;
            }
        }
    }
}
