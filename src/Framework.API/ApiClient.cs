using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using System.Text;
using Framework.Reports;

namespace Framework.API;

/// <summary>
/// HTTP client wrapper for making REST API calls in tests.
/// Sends prepared <see cref="HttpRequestMessage"/> instances, records sanitized request/response
/// exchanges in <see cref="Framework.Reports.RuntimeContext"/>, and exposes bearer-token
/// visibility control for reporting.
/// </summary>
public class APIClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<APIClient> _logger;

    public bool ShowBearerToken { get; set; } = true;

    /// <summary>Serilog logger injected per-test by the test base for console/file output.</summary>
    public Serilog.ILogger? SerilogLogger { get; set; }

    public APIClient(HttpClient httpClient, ILogger<APIClient>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? NullLogger<APIClient>.Instance;
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request), "HttpRequestMessage cannot be null.");
        }

        if (request.RequestUri is null)
        {
            throw new InvalidOperationException("Request URI must be set before sending the request.");
        }

        _logger.LogInformation("Sending API request: {Method} {Url}", request.Method, request.RequestUri);
        SerilogLogger?.Information("[API] >> {Method} {Url}", request.Method, request.RequestUri);
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError("HTTP request failed: {Message}", ex.Message);
            throw new InvalidOperationException($"Failed to send HTTP request to {request.RequestUri}: {ex.Message}", ex);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogError("HTTP request was cancelled.");
            throw new InvalidOperationException($"HTTP request to {request.RequestUri} was cancelled.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError("Unexpected error while sending request: {Message}", ex.Message);
            throw new InvalidOperationException($"Unexpected error while sending request to {request.RequestUri}: {ex.Message}", ex);
        }

        _logger.LogInformation("Received API response: {StatusCode}", (int)response.StatusCode);
        SerilogLogger?.Information("[API] << {StatusCode} {ReasonPhrase}", (int)response.StatusCode, response.ReasonPhrase);
        var requestDump = await FormatRequestAsync(request, cancellationToken);
        try
        {
            requestDump = APIContentSanitizer.SanitizeDump(requestDump, ShowBearerToken);
        }
        catch (Exception ex)
        {
            _logger.LogError("Request content could not be sanitized; details redacted for security. Error: {Message}", ex.Message);
            requestDump = "[Request content could not be sanitised.]";
        }

        var responseDump = await FormatResponseAsync(response, cancellationToken);
        try
        {
            responseDump = APIContentSanitizer.SanitizeDump(responseDump, ShowBearerToken);
        }
        catch (Exception ex)
        {
            _logger.LogError("Response content could not be sanitized; details redacted for security. Error: {Message}", ex.Message);
            responseDump = "[Response content could not be sanitised.]";
        }

        RuntimeContext.RecordApiExchange(
            requestDump,
            responseDump);
        return response;
    }

    public void Dispose()
    {
        // Do not dispose the injected HttpClient here.
        // The composition root (DI container or test fixture) is responsible for managing
        // the lifecycle of the injected HttpClient. Disposing it here would break other
        // consumers in a shared test runtime.
        // Intentionally no-op.
    }

    private static async Task<string> FormatRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request), "HttpRequestMessage cannot be null while formatting.");
        }

        var builder = new StringBuilder();
        var requestUri = request.RequestUri?.ToString() ?? "<unknown URI>";
        builder.AppendLine($"{request.Method} {requestUri}");

        foreach (var header in request.Headers)
        {
            builder.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
        }

        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                builder.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
            }

            builder.AppendLine();
            builder.AppendLine(await request.Content.ReadAsStringAsync(cancellationToken));
        }

        return builder.ToString().Trim();
    }

    private static async Task<string> FormatResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response is null)
        {
            throw new ArgumentNullException(nameof(response), "HttpResponseMessage cannot be null while formatting.");
        }

        var builder = new StringBuilder();
        var reasonPhrase = response.ReasonPhrase ?? "Unknown";
        builder.AppendLine($"HTTP {(int)response.StatusCode} {reasonPhrase}");

        foreach (var header in response.Headers)
        {
            builder.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
        }

        if (response.Content is not null)
        {
            foreach (var header in response.Content.Headers)
            {
                builder.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
            }

            builder.AppendLine();
            builder.AppendLine(await response.Content.ReadAsStringAsync(cancellationToken));
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Validates that the actual status code matches the expected code.
    /// Direct comparison - fails assertion if they don't match.
    /// </summary>
    /// <param name="actualStatus">The actual HTTP status code received</param>
    /// <param name="expectedCode">Expected HTTP status code (e.g., 200, 201, 400)</param>
    public static void ValidateStatusCode(System.Net.HttpStatusCode actualStatus, int expectedCode)
    {
        int actualCode = (int)actualStatus;
        if (actualCode != expectedCode)
        {
            var message = $"Status code validation failed. Expected : {expectedCode} but got : {actualCode}";
            NUnit.Framework.Assert.Fail(message);
        }
    }

}
