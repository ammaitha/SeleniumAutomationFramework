using System.Collections.Concurrent;

namespace Framework.Reports;

public static class RuntimeContext
{
    private static readonly AsyncLocal<string?> CurrentBrowserName = new();
    private static readonly AsyncLocal<string?> CurrentTestType = new();
    private static readonly AsyncLocal<ApiExchange?> LastApiExchange = new();
    private static readonly ConcurrentDictionary<string, byte> Browsers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> TestTypes = new(StringComparer.OrdinalIgnoreCase);

    static RuntimeContext()
    {
        StartTime = DateTimeOffset.UtcNow;
    }

    public static DateTimeOffset StartTime { get; private set; }

    public static string BrowserName
    {
        get => string.IsNullOrWhiteSpace(CurrentBrowserName.Value) ? "N/A" : CurrentBrowserName.Value!;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "N/A" : value.Trim();
            CurrentBrowserName.Value = normalized;
            Browsers[normalized] = 0;
        }
    }

    public static string TestType
    {
        get => string.IsNullOrWhiteSpace(CurrentTestType.Value) ? "Hybrid" : CurrentTestType.Value!;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "Hybrid" : value.Trim();
            CurrentTestType.Value = normalized;
            TestTypes[normalized] = 0;
        }
    }

    public static string GetBrowsersSummary()
    {
        return JoinValues(Browsers.Keys, "N/A");
    }

    public static string GetTestTypesSummary()
    {
        return JoinValues(TestTypes.Keys, "Hybrid");
    }

    public static void RecordApiExchange(string request, string response)
    {
        LastApiExchange.Value = new ApiExchange(request, response);
    }

    public static ApiExchange? GetLastApiExchange()
    {
        return LastApiExchange.Value;
    }

    public static void ClearTestScope()
    {
        CurrentBrowserName.Value = null;
        CurrentTestType.Value = null;
        LastApiExchange.Value = null;
    }

    private static string JoinValues(IEnumerable<string> values, string fallback)
    {
        var distinct = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return distinct.Length == 0 ? fallback : string.Join(", ", distinct);
    }

    public sealed record ApiExchange(string Request, string Response);
}
