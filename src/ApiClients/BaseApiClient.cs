using System.Net;
using System.Text;
using Framework.API;
using Framework.Reports;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Framework.API.Clients;

/// <summary>
/// Base API client abstraction with common HTTP send helpers, logging, and assertions.
/// Authentication behavior is test-driven: requests use the token already present in ApiSessionContext.
/// </summary>
public abstract class BaseApiClient
{
    protected BaseApiClient(APIClient apiClient, Serilog.ILogger logger)
    {
        ApiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient), "ApiClient cannot be null.");
        Logger = logger ?? throw new ArgumentNullException(nameof(logger), "Logger cannot be null.");
    }

    protected APIClient ApiClient { get; }
    protected Serilog.ILogger Logger { get; }

    protected async Task<ApiCallResult> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        IDictionary<string, string?>? queryParams = null,
        bool requiresAuth = false,
        CancellationToken cancellationToken = default)
    {
        if (method is null)
        {
            throw new ArgumentNullException(nameof(method), "HttpMethod cannot be null.");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("API path cannot be null or empty.", nameof(path));
        }

        var relativeUrl = BuildRelativeUrl(path, queryParams);
        var capturedAccessToken = requiresAuth ? ApiSessionContext.Current.CurrentAccessToken : null;

        ReportManager.AddStep($"{method} {relativeUrl}");

            var requestBuilder = new APIRequestBuilder()
                .WithMethod(method)
                .WithEndpoint(relativeUrl);

            if (requiresAuth && !string.IsNullOrWhiteSpace(capturedAccessToken))
            {
                // Capture token before entering the reporting step to avoid AsyncLocal context loss.
                requestBuilder.WithBearerToken(capturedAccessToken);
            }

            if (body is not null)
            {
                requestBuilder.WithJsonBody(body);
            }

            using var request = requestBuilder.Build();

            string requestText;
            try
            {
                requestText = await FormatRequestAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to format request for {method} {relativeUrl}.", ex);
            }

            try
            {
                requestText = APIContentSanitizer.SanitizeDump(requestText, ApiClient.ShowBearerToken);
            }
            catch (Exception ex)
            {
                Logger.Error("Request content could not be sanitized; details redacted for security. Error: {Message}", ex.Message);
                requestText = "[Request content could not be sanitised.]";
            }

            Logger.Information("[API] Request: {Method} {Url}\n{RequestDetails}", method, relativeUrl, requestText);
            ReportHelper.AttachContent($"API Request - {method} {relativeUrl}", "text/plain", requestText, "txt");

            HttpResponseMessage response;
            try
            {
                response = await ApiClient.SendAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"API call failed for {method} {relativeUrl}: {ex.Message}", ex);
            }

            string responseBody;
            try
            {
                responseBody = response.Content is null
                    ? string.Empty
                    : await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to read response body from {method} {relativeUrl}.", ex);
            }

            string responseText;
            try
            {
                responseText = FormatResponse(response, responseBody);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to format response from {method} {relativeUrl}.", ex);
            }

            try
            {
                responseText = APIContentSanitizer.SanitizeDump(responseText, ApiClient.ShowBearerToken);
            }
            catch (Exception ex)
            {
                Logger.Error("Response content could not be sanitized; details redacted for security. Error: {Message}", ex.Message);
                responseText = "[Response content could not be sanitised.]";
            }

            Logger.Information("[API] Response: {StatusCode}\n{ResponseDetails}", (int)response.StatusCode, responseText);
            ReportHelper.AttachContent($"API Response - {method} {relativeUrl}", "text/plain", responseText, "txt");

            try
            {
                RuntimeContext.RecordApiExchange(requestText, responseText);
            }
            catch (Exception ex)
            {
                Logger.Warning("Failed to record API exchange: {Message}", ex.Message);
            }

            return new ApiCallResult(response.StatusCode, responseBody);
    }

    protected static JObject ParseObject(ApiCallResult result)
    {
        if (result is null)
        {
            throw new ArgumentNullException(nameof(result), "ApiCallResult cannot be null while parsing.");
        }

        if (string.IsNullOrWhiteSpace(result.ResponseBody))
        {
            throw new InvalidOperationException("Response body is empty. Cannot parse JSON from an empty response.");
        }

        try
        {
            return JObject.Parse(result.ResponseBody);
        }
        catch (JsonException ex)
        {
            string sanitizedContent;
            try
            {
                sanitizedContent = APIContentSanitizer.SanitizeDump(result.ResponseBody, showBearerToken: false);
                sanitizedContent = sanitizedContent.Substring(0, Math.Min(100, sanitizedContent.Length)) + "...";
            }
            catch
            {
                sanitizedContent = "[Response content could not be included in error message for security reasons.]";
            }

            throw new InvalidOperationException($"Failed to parse response body as JSON. Sanitized content: {sanitizedContent}", ex);
        }
    }

    private static string BuildRelativeUrl(string path, IDictionary<string, string?>? queryParams)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path cannot be null or empty.", nameof(path));
        }

        var cleanPath = path.StartsWith("/", StringComparison.Ordinal) ? path : $"/{path}";

        if (queryParams is null || queryParams.Count == 0)
        {
            return cleanPath;
        }

        var filtered = queryParams
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value!)}")
            .ToArray();

        return filtered.Length == 0
            ? cleanPath
            : $"{cleanPath}?{string.Join("&", filtered)}";
    }

    private static async Task<string> FormatRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var requestUri = request.RequestUri?.ToString() ?? "<unknown URI>";
        sb.AppendLine($"{request.Method} {requestUri}");

        foreach (var header in request.Headers)
        {
            sb.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
        }

        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                sb.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
            }

            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            sb.AppendLine();
            sb.AppendLine(PrettifyJson(body));
        }

        return sb.ToString().Trim();
    }

    private static string FormatResponse(HttpResponseMessage response, string body)
    {
        var sb = new StringBuilder();
        var reasonPhrase = response.ReasonPhrase ?? "Unknown";
        sb.AppendLine($"HTTP {(int)response.StatusCode} {reasonPhrase}");

        foreach (var header in response.Headers)
        {
            sb.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
        }

        if (response.Content is not null)
        {
            foreach (var header in response.Content.Headers)
            {
                sb.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
            }

            sb.AppendLine();
            sb.AppendLine(PrettifyJson(body));
        }

        return sb.ToString().Trim();
    }

    private static string PrettifyJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        try
        {
            var token = JToken.Parse(json);
            return token.ToString(Formatting.Indented);
        }
        catch
        {
            return json;
        }
    }
}

public sealed record ApiCallResult(HttpStatusCode StatusCode, string ResponseBody);
