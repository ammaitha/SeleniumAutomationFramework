using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Text.Json;
using Framework.Core.Configuration;
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
    private const int JsPollingIntervalMs = 150;
    private const int JsMaxCapturedEntries = 300;
    private const int JsMaxBodyLength = 200000;

    public static bool IsNetworkCaptureSupported(IWebDriver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        return driver is IDevTools
            || (IsJavaScriptFallbackEnabled() && driver is IJavaScriptExecutor);
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
            catch (Exception ex) when (_suppressCaptureFailures)
            {
                _logger.Warning(
                    ex,
                    "[Network] Suppressing capture failure for browser {Browser}. Continuing test flow without network assertions.",
                    _browserName);
                break;
            }
        }
        return responses;
    }

    private readonly ILogger _logger;
    private readonly IWebDriver _driver;
    private readonly IJavaScriptExecutor? _jsExecutor;
    private readonly NetworkAdapter? _network;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingCapture> _captures = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _jsInstallLock = new(1, 1);
    private readonly bool _suppressCaptureFailures;
    private readonly string _browserName;
    private bool _networkEnabled;
    private bool _useJavaScriptCapture;
    private bool _jsTapInstalled;
    private bool _disposed;
    public bool CanCaptureNetwork { get; }

    public NetworkHelper(IWebDriver driver, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(driver);
        _logger = logger ?? Serilog.Log.Logger;
        _driver = driver;
        _jsExecutor = driver as IJavaScriptExecutor;
        _browserName = ResolveBrowserName(driver);
        _suppressCaptureFailures = ShouldSuppressCaptureFailures(driver);

        if (driver is IDevTools devTools)
        {
            CanCaptureNetwork = true;

            var devToolsSession = devTools.GetDevToolsSession();
            var domains = devToolsSession.GetVersionSpecificDomains<OpenQA.Selenium.DevTools.V145.DevToolsSessionDomains>();
            _network = domains.Network;

            _network.ResponseReceived += OnResponseReceived;
            _network.LoadingFinished += OnLoadingFinished;
            return;
        }

        if (IsJavaScriptFallbackEnabled() && _jsExecutor is not null)
        {
            CanCaptureNetwork = true;
            _useJavaScriptCapture = true;
            _logger.Information("[Network] DevTools/BiDi are unavailable. Falling back to JavaScript runtime network capture.");

            // Install immediately so requests triggered right after helper construction
            // are captured (callers often click before invoking WaitForResponse).
            try
            {
                EnsureJsTapInstalledAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is WebDriverException or TaskCanceledException)
            {
                _logger.Debug(ex, "[Network] Initial JavaScript network tap installation will be retried during waits.");
            }

            return;
        }

        CanCaptureNetwork = false;
        _logger.Warning("[Network] Network capture is not supported for the active browser driver.");
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

        if (_useJavaScriptCapture)
        {
            return WaitForResponseViaJavaScriptSafe(urlPattern, predicate, timeoutSeconds);
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
        _jsInstallLock.Dispose();
    }

    private static bool IsJavaScriptFallbackEnabled()
    {
        var raw = Environment.GetEnvironmentVariable("Network__EnableJsFallback");
        if (TryParseFlexibleBool(raw, out var envValue))
        {
            return envValue;
        }

        return ConfigManager.GetBoolOrDefault("TestSettings:Network:EnableJsFallback", false);
    }

    private static bool ShouldSuppressCaptureFailures(IWebDriver driver)
    {
        return IsFirefox(driver)
            && ConfigManager.GetBoolOrDefault("TestSettings:Network:SuppressFirefoxCaptureFailures", true);
    }

    private static bool IsFirefox(IWebDriver driver)
    {
        var browserName = ResolveBrowserName(driver);
        return browserName.Equals("firefox", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveBrowserName(IWebDriver driver)
    {
        if (driver is IHasCapabilities hasCapabilities)
        {
            var raw = hasCapabilities.Capabilities.GetCapability("browserName")?.ToString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                return raw.Trim();
            }
        }

        return "unknown";
    }

    private static bool TryParseFlexibleBool(string? raw, out bool value)
    {
        if (bool.TryParse(raw, out value))
        {
            return true;
        }

        if (string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }

        if (string.Equals(raw, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "no", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }

        value = false;
        return false;
    }

    private async Task<NetworkResponse> WaitForResponseViaJavaScript(string urlPattern, Func<NetworkResponse, bool> predicate, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        _logger.Information("[Network] Waiting for response matching {UrlPattern}", urlPattern);

        while (DateTime.UtcNow < deadline)
        {
            ThrowIfDisposed();

            await EnsureJsTapInstalledAsync().ConfigureAwait(false);
            var captured = DrainJsCapturedResponses();

            foreach (var response in captured)
            {
                if (!IsUrlMatch(response.Url, urlPattern))
                {
                    continue;
                }

                if (!predicate(response))
                {
                    continue;
                }

                _logger.Information("[Network] Matched response {StatusCode} {Url}", response.StatusCode, response.Url);
                return response;
            }

            await Task.Delay(JsPollingIntervalMs).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out after {timeoutSeconds}s waiting for a response matching '{urlPattern}'.");
    }

    private async Task<NetworkResponse> WaitForResponseViaJavaScriptSafe(string urlPattern, Func<NetworkResponse, bool> predicate, int timeoutSeconds)
    {
        try
        {
            return await WaitForResponseViaJavaScript(urlPattern, predicate, timeoutSeconds).ConfigureAwait(false);
        }
        catch (Exception ex) when (_suppressCaptureFailures)
        {
            _logger.Warning(
                ex,
                "[Network] JavaScript capture failed for browser {Browser}. Falling back to API+UI-only validation.",
                _browserName);

            throw new TimeoutException(
                $"Network capture failed for browser '{_browserName}'. Continuing without network assertions.",
                ex);
        }
    }

    private async Task EnsureJsTapInstalledAsync()
    {
        if (!_useJavaScriptCapture || _jsExecutor is null)
        {
            return;
        }

        if (_jsTapInstalled)
        {
            try
            {
                var stillInstalled = _jsExecutor.ExecuteScript("return !!window.__networkTapInstalled;");
                if (stillInstalled is bool b && b)
                {
                    return;
                }
            }
            catch (WebDriverException)
            {
                // Navigation may be in progress; re-attempt installation in the next polling cycle.
            }

            _jsTapInstalled = false;
        }

        await _jsInstallLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_jsTapInstalled)
            {
                return;
            }

            _jsExecutor.ExecuteScript(
                "if (!window.__networkTapInstalled) {" +
                "  window.__networkTapInstalled = true;" +
                "  window.__networkTapQueue = window.__networkTapQueue || [];" +
                "  window.__networkTapPush = function(item) {" +
                "    try {" +
                "      const q = window.__networkTapQueue;" +
                "      q.push(item);" +
                $"      if (q.length > {JsMaxCapturedEntries}) {{ q.splice(0, q.length - {JsMaxCapturedEntries}); }}" +
                "    } catch (e) { }" +
                "  };" +
                "  const originalFetch = window.fetch;" +
                "  if (typeof originalFetch === 'function' && !window.__networkTapFetchWrapped) {" +
                "    window.__networkTapFetchWrapped = true;" +
                "    window.fetch = async function(input, init) {" +
                "      const started = Date.now();" +
                "      const method = (init && init.method) ? String(init.method) : 'GET';" +
                "      let url = '';" +
                "      try {" +
                "        if (typeof input === 'string') url = input;" +
                "        else if (input && input.url) url = input.url;" +
                "      } catch (e) { }" +
                "      try {" +
                "        const response = await originalFetch.apply(this, arguments);" +
                "        let body = '';" +
                "        try { body = await response.clone().text(); } catch (e) { }" +
                $"        if (body && body.length > {JsMaxBodyLength}) body = body.substring(0, {JsMaxBodyLength});" +
                "        window.__networkTapPush({ transport: 'fetch', method: method, url: String(url || ''), status: Number(response.status || 0), body: body || '', started: started, ended: Date.now() });" +
                "        return response;" +
                "      } catch (err) {" +
                "        window.__networkTapPush({ transport: 'fetch', method: method, url: String(url || ''), status: 0, body: String(err || ''), started: started, ended: Date.now() });" +
                "        throw err;" +
                "      }" +
                "    };" +
                "  }" +
                "  if (!window.__networkTapXhrWrapped) {" +
                "    window.__networkTapXhrWrapped = true;" +
                "    const originalOpen = XMLHttpRequest.prototype.open;" +
                "    const originalSend = XMLHttpRequest.prototype.send;" +
                "    XMLHttpRequest.prototype.open = function(method, url) {" +
                "      try { this.__networkTapMethod = method; this.__networkTapUrl = url; } catch (e) { }" +
                "      return originalOpen.apply(this, arguments);" +
                "    };" +
                "    XMLHttpRequest.prototype.send = function() {" +
                "      const started = Date.now();" +
                "      try {" +
                "        this.addEventListener('loadend', function() {" +
                "          let body = '';" +
                "          try { body = this.responseText || ''; } catch (e) { }" +
                $"          if (body && body.length > {JsMaxBodyLength}) body = body.substring(0, {JsMaxBodyLength});" +
                "          window.__networkTapPush({" +
                "            transport: 'xhr'," +
                "            method: String(this.__networkTapMethod || 'GET')," +
                "            url: String(this.__networkTapUrl || '')," +
                "            status: Number(this.status || 0)," +
                "            body: body || ''," +
                "            started: started," +
                "            ended: Date.now()" +
                "          });" +
                "        });" +
                "      } catch (e) { }" +
                "      return originalSend.apply(this, arguments);" +
                "    };" +
                "  }" +
                "}");

            _jsTapInstalled = true;
        }
        catch (WebDriverException)
        {
            _jsTapInstalled = false;
        }
        finally
        {
            _jsInstallLock.Release();
        }
    }

    private IReadOnlyCollection<NetworkResponse> DrainJsCapturedResponses()
    {
        if (_jsExecutor is null)
        {
            return Array.Empty<NetworkResponse>();
        }

        try
        {
            var rawJson = _jsExecutor.ExecuteScript(
                "const q = window.__networkTapQueue || []; const out = q.splice(0, q.length); return JSON.stringify(out);")?.ToString();

            if (string.IsNullOrWhiteSpace(rawJson))
            {
                return Array.Empty<NetworkResponse>();
            }

            var responses = new List<NetworkResponse>();
            using var document = JsonDocument.Parse(rawJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<NetworkResponse>();
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                var url = element.TryGetProperty("url", out var urlProp)
                    ? (urlProp.GetString() ?? string.Empty)
                    : string.Empty;

                var body = element.TryGetProperty("body", out var bodyProp)
                    ? (bodyProp.GetString() ?? string.Empty)
                    : string.Empty;

                var statusCode = element.TryGetProperty("status", out var statusProp) && statusProp.TryGetInt32(out var status)
                    ? status
                    : 0;

                responses.Add(new NetworkResponse
                {
                    Url = url,
                    StatusCode = statusCode,
                    Body = body
                });
            }

            return responses;
        }
        catch (Exception ex) when (ex is WebDriverException or JsonException)
        {
            return Array.Empty<NetworkResponse>();
        }
    }

    private static bool IsUrlMatch(string url, string pattern)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        if (!IsRegexPattern(pattern))
        {
            return url.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            return Regex.IsMatch(url, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch
        {
            return url.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }
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
