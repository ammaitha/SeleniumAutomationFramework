using OpenQA.Selenium;

namespace Framework.Core.Utilities;

/// <summary>
/// Utility to quickly initialize an authenticated browser context for hybrid tests
/// by applying API-issued auth state (token, user storage, cookies) before UI steps.
/// This avoids repeated UI login flows and keeps API+UI test setup fast and deterministic.
/// </summary>
public sealed class BrowserSessionBootstrapper
{
    private readonly IWebDriver _driver;

    public BrowserSessionBootstrapper(IWebDriver driver)
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
    }

    public void BootstrapAuthenticatedSession(
        Uri applicationBaseUri,
        string authToken,
        string userJson,
        IEnumerable<string>? setCookieHeaders = null,
        IEnumerable<string>? tokenStorageKeys = null,
        Action? waitAfterNavigation = null)
    {
        ArgumentNullException.ThrowIfNull(applicationBaseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(authToken);
        userJson ??= "{}";

        NavigateToBase(applicationBaseUri, waitAfterNavigation);
        ApplySetCookieHeaders(applicationBaseUri.Host, setCookieHeaders);
        SeedBrowserStorage(authToken, userJson, tokenStorageKeys);
        NavigateToBase(applicationBaseUri, waitAfterNavigation);
    }

    public void NavigateToBase(Uri applicationBaseUri, Action? waitAfterNavigation)
    {
        _driver.Navigate().GoToUrl(applicationBaseUri);
        waitAfterNavigation?.Invoke();
    }

    public void SeedBrowserStorage(string authToken, string userJson, IEnumerable<string>? tokenStorageKeys)
    {
        var keys = (tokenStorageKeys ?? new[] { "eventhub_token", "token", "accessToken", "authToken", "jwt" })
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        ((IJavaScriptExecutor)_driver).ExecuteScript(
            "const token = arguments[0];" +
            "const user = arguments[1];" +
            "const keys = arguments[2];" +
            "for (let i = 0; i < keys.length; i++) {" +
            "  const key = keys[i];" +
            "  window.localStorage.setItem(key, token);" +
            "  window.sessionStorage.setItem(key, token);" +
            "}" +
            "window.localStorage.setItem('isAuthenticated', 'true');" +
            "window.localStorage.setItem('user', user);" +
            "window.localStorage.setItem('currentUser', user);" +
            "window.sessionStorage.setItem('isAuthenticated', 'true');" +
            "window.sessionStorage.setItem('user', user);" +
            "window.sessionStorage.setItem('currentUser', user);",
            authToken,
            userJson,
            keys);
    }

    public void ApplySetCookieHeaders(string currentHost, IEnumerable<string>? setCookieHeaders)
    {
        if (setCookieHeaders == null)
        {
            return;
        }

        foreach (var setCookie in setCookieHeaders)
        {
            if (string.IsNullOrWhiteSpace(setCookie))
            {
                continue;
            }

            var segments = setCookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
            {
                continue;
            }

            var nameValue = segments[0];
            var separatorIndex = nameValue.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var name = nameValue[..separatorIndex];
            var value = nameValue[(separatorIndex + 1)..];
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            string? domain = currentHost;
            string path = "/";

            foreach (var attr in segments.Skip(1))
            {
                if (attr.StartsWith("Domain=", StringComparison.OrdinalIgnoreCase))
                {
                    var rawDomain = attr[7..].Trim();
                    domain = rawDomain.TrimStart('.');
                }
                else if (attr.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))
                {
                    path = attr[5..].Trim();
                }
            }

            if (!string.IsNullOrWhiteSpace(domain)
                && !currentHost.EndsWith(domain, StringComparison.OrdinalIgnoreCase)
                && !domain.EndsWith(currentHost, StringComparison.OrdinalIgnoreCase))
            {
                domain = currentHost;
            }

            _driver.Manage().Cookies.AddCookie(new Cookie(name, value, domain, path, null));
        }
    }
}
