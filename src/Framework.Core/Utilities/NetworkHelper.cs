using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using OpenQA.Selenium;
using OpenQA.Selenium.DevTools;
using OpenQA.Selenium.DevTools.V145.Network;
using Serilog;

namespace Framework.Core.Utilities;

/// <summary>
/// Reusable DevTools helper for capturing and asserting browser network traffic in UI/hybrid tests.
/// Enables API+UI+network validation by waiting for matching responses and verifying status/body
/// without adding flaky sleeps or ad-hoc driver-specific logic in test methods.
/// </summary>
public sealed class NetworkHelper : IDisposable
{
    public static bool IsNetworkCaptureSupported(IWebDriver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        return driver is IDevTools;
    }

    public static bool ShouldValidateNetwork(IWebDriver driver, ILogger? logger = null, string browserName = "unknown")
    {
        var canCaptureNetwork = IsNetworkCaptureSupported(driver);
        if (!canCaptureNetwork)
        {
            (logger ?? Serilog.Log.Logger).Warning(
                "[Network] Network capture is not supported for browser {Browser}. Continuing with API+UI assertions.",
                browserName);
        }

        return canCaptureNetwork;
    }


    /// <summary>
    /// Returns true if the exception represents a transient HTTP transport failure
    /// that is safe to retry (e.g. HttpRequestException, socket/connection errors).
    /// </summary>
    public static bool IsTransientTransportFailure(Exception ex)
    {
        if (ex is HttpRequestException)
            return true;

        if (ex is InvalidOperationException ioe
            && ioe.Message.Contains("Failed to send HTTP request", StringComparison.OrdinalIgnoreCase))
            return true;

        return ex.InnerException is not null && IsTransientTransportFailure(ex.InnerException);
    }

    /// <summary>
    /// Executes an operation with configurable retry behavior.
    /// Useful for transient transport/network failures in hybrid flows.
    /// </summary>
    public static T ExecuteWithRetry<T>(
        Func<T> operation,
        Func<Exception, bool> shouldRetry,
        int maxAttempts = 3,
        TimeSpan? delay = null,
        Action<Exception, int, int>? onRetry = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(shouldRetry);

        if (maxAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "maxAttempts must be greater than 0.");

        var retryDelay = delay ?? TimeSpan.FromSeconds(2);
        Exception? last = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return operation();
            }
            catch (Exception ex) when (attempt < maxAttempts && shouldRetry(ex))
            {
                last = ex;
                onRetry?.Invoke(ex, attempt, maxAttempts);
                Thread.Sleep(retryDelay);
            }
            catch (Exception ex)
            {
                last = ex;
                break;
            }
        }

        throw new InvalidOperationException($"Operation failed after {maxAttempts} attempts.", last);
    }

    /// <summary>
    /// Waits for multiple network responses matching a URL pattern. Returns all matches up to maxCount or until timeout.
    /// </summary>
    public async Task<List<NetworkResponse>> WaitForAllResponses(string urlPattern, int maxCount, int timeoutSeconds = 10)
    {
        if (string.IsNullOrWhiteSpace(urlPattern))
            throw new ArgumentException("URL pattern cannot be null or empty.", nameof(urlPattern));
        if (maxCount <= 0)
            throw new ArgumentException("maxCount must be positive.", nameof(maxCount));

        var responses = new List<NetworkResponse>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        int captured = 0;

        while (captured < maxCount && !cts.IsCancellationRequested)
        {
            try
            {
                var resp = await WaitForResponse(urlPattern, timeoutSeconds: timeoutSeconds).ConfigureAwait(false);
                responses.Add(resp);
                captured++;
            }
            catch (TimeoutException)
            {
                break;
            }
        }
        return responses;
    }

    private readonly ILogger _logger;
    private readonly NetworkAdapter? _network;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingCapture> _captures = new(StringComparer.OrdinalIgnoreCase);
    private bool _networkEnabled;
    private bool _disposed;
    public bool CanCaptureNetwork { get; }

    public NetworkHelper(IWebDriver driver, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(driver);
        _logger = logger ?? Serilog.Log.Logger;

        if (driver is not IDevTools devTools)
        {
            CanCaptureNetwork = false;
            _logger.Warning("[Network] DevTools is not available for this browser. Network capture assertions will be skipped.");
            return;
        }

        CanCaptureNetwork = true;

        var devToolsSession = devTools.GetDevToolsSession();
        var domains = devToolsSession.GetVersionSpecificDomains<OpenQA.Selenium.DevTools.V145.DevToolsSessionDomains>();
        _network = domains.Network;

        _network.ResponseReceived += OnResponseReceived;
        _network.LoadingFinished += OnLoadingFinished;
    }

    public Task<NetworkResponse> WaitForResponse(string urlPattern, int timeoutSeconds = 10)
    {
        return WaitForResponse(urlPattern, _ => true, timeoutSeconds);
    }

    public Task<NetworkResponse> WaitForResponse(string urlPattern, Func<NetworkResponse, bool> predicate)
    {
        return WaitForResponse(urlPattern, predicate, 10);
    }

    public Task<NetworkResponse> WaitForResponse(string urlPattern, Func<NetworkResponse, bool> predicate, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(urlPattern))
        {
            throw new ArgumentException("URL pattern cannot be null or empty.", nameof(urlPattern));
        }

        ArgumentNullException.ThrowIfNull(predicate);
        ThrowIfDisposed();

        if (!CanCaptureNetwork)
        {
            throw new NotSupportedException("Network capture is not available for the active browser driver.");
        }

        EnsureNetworkEnabledAsync().GetAwaiter().GetResult();

        var captureId = Guid.NewGuid().ToString("N");
        var capture = new PendingCapture(urlPattern, predicate);
        _captures[captureId] = capture;

        _logger.Information("[Network] Waiting for response matching {UrlPattern}", urlPattern);
        return AwaitCaptureAsync(captureId, capture, urlPattern, timeoutSeconds);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_network is not null)
        {
            _network.ResponseReceived -= OnResponseReceived;
            _network.LoadingFinished -= OnLoadingFinished;
        }
        _initializationLock.Dispose();
    }

    public async Task<NetworkResponse> AwaitCaptureAsync(string captureId, PendingCapture capture, string urlPattern, int timeoutSeconds)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            var completed = await Task.WhenAny(capture.Completion.Task, Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token)).ConfigureAwait(false);

            if (completed != capture.Completion.Task)
            {
                throw new TimeoutException($"Timed out after {timeoutSeconds}s waiting for a response matching '{urlPattern}'.");
            }

            return await capture.Completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!capture.Completion.Task.IsCompleted)
        {
            throw new TimeoutException($"Timed out after {timeoutSeconds}s waiting for a response matching '{urlPattern}'.");
        }
        finally
        {
            _captures.TryRemove(captureId, out _);
        }
    }

    public void OnResponseReceived(object? sender, ResponseReceivedEventArgs e)
    {
        var requestId = e.RequestId?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        foreach (var entry in _captures)
        {
            if (!entry.Value.IsMatch(e.Response.Url))
            {
                continue;
            }

            if (entry.Value.TryBindRequest(requestId, e))
            {
                _logger.Information("[Network] Matched response {StatusCode} {Url}", (int)e.Response.Status, e.Response.Url);
            }
        }
    }

    public void OnLoadingFinished(object? sender, LoadingFinishedEventArgs e)
    {
        var requestId = e.RequestId?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        if (_network is null)
        {
            return;
        }

        foreach (var entry in _captures)
        {
            if (entry.Value.TryCompleteOnLoad(requestId, _network))
            {
                break;
            }
        }
    }

    public async Task EnsureNetworkEnabledAsync()
    {
        if (!CanCaptureNetwork || _network is null)
        {
            throw new NotSupportedException("Network capture is not available for the active browser driver.");
        }

        if (_networkEnabled)
        {
            return;
        }

        await _initializationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_networkEnabled)
            {
                return;
            }

            await _network.Enable(new EnableCommandSettings()).ConfigureAwait(false);
            _networkEnabled = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(NetworkHelper));
        }
    }

    public static bool IsRegexPattern(string pattern)
    {
        return pattern.Contains('^')
            || pattern.Contains('$')
            || pattern.Contains('[')
            || pattern.Contains(']')
            || pattern.Contains('(')
            || pattern.Contains(')')
            || pattern.Contains('|')
            || pattern.Contains('\\');
    }

    public sealed class PendingCapture
    {
        private readonly string _urlPattern;
        private readonly Func<NetworkResponse, bool> _predicate;
        private readonly object _gate = new();
        private string? _requestId;
        private ResponseReceivedEventArgs? _responseEvent;
        private bool _completed;

        public PendingCapture(string urlPattern, Func<NetworkResponse, bool> predicate)
        {
            _urlPattern = urlPattern;
            _predicate = predicate;
            Completion = new TaskCompletionSource<NetworkResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public TaskCompletionSource<NetworkResponse> Completion { get; }

        public bool IsMatch(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            if (!IsRegexPattern(_urlPattern))
            {
                return url.Contains(_urlPattern, StringComparison.OrdinalIgnoreCase);
            }

            try
            {
                return Regex.IsMatch(url, _urlPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch
            {
                return url.Contains(_urlPattern, StringComparison.OrdinalIgnoreCase);
            }
        }

        public bool TryBindRequest(string requestId, ResponseReceivedEventArgs responseEvent)
        {
            lock (_gate)
            {
                if (_completed || !string.IsNullOrWhiteSpace(_requestId))
                {
                    return false;
                }

                _requestId = requestId;
                _responseEvent = responseEvent;
                return true;
            }
        }

        public bool TryCompleteOnLoad(string requestId, NetworkAdapter network)
        {
            ResponseReceivedEventArgs? responseEvent;

            lock (_gate)
            {
                if (_completed || !string.Equals(_requestId, requestId, StringComparison.OrdinalIgnoreCase) || _responseEvent is null)
                {
                    return false;
                }

                responseEvent = _responseEvent;
            }

            return CompleteAsync(responseEvent, network).GetAwaiter().GetResult();
        }

        private async Task<bool> CompleteAsync(ResponseReceivedEventArgs responseEvent, NetworkAdapter network)
        {
            try
            {
                var bodyResponse = await network.GetResponseBody(
                    new GetResponseBodyCommandSettings { RequestId = responseEvent.RequestId }).ConfigureAwait(false);

                var body = bodyResponse.Base64Encoded
                    ? DecodeBase64Body(bodyResponse.Body)
                    : bodyResponse.Body ?? string.Empty;

                var networkResponse = new NetworkResponse
                {
                    Url = responseEvent.Response.Url ?? string.Empty,
                    StatusCode = (int)responseEvent.Response.Status,
                    Body = body
                };

                if (!_predicate(networkResponse))
                {
                    lock (_gate)
                    {
                        // Keep waiting for subsequent matching responses if this one does not satisfy predicate.
                        _requestId = null;
                        _responseEvent = null;
                    }
                    return false;
                }

                lock (_gate)
                {
                    if (_completed)
                    {
                        return true;
                    }

                    _completed = true;
                }

                Completion.TrySetResult(networkResponse);
                return true;
            }
            catch (Exception)
            {
                lock (_gate)
                {
                    // Some responses (e.g., preflight/short-lived resources) may not have body available.
                    // Continue waiting instead of failing the entire capture immediately.
                    _requestId = null;
                    _responseEvent = null;
                }

                return false;
            }
        }

        private static string DecodeBase64Body(string body)
        {
            try
            {
                var bytes = Convert.FromBase64String(body);
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return body;
            }
        }
    }
}

/// <summary>
/// Captured network response payload.
/// </summary>
public sealed class NetworkResponse
{
    public string Url { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string Body { get; set; } = string.Empty;
}
