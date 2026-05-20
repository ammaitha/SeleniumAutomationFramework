using OpenQA.Selenium;
using Framework.Core.Utilities;

namespace UITests.Pages;

/// <summary>
/// Page object for the event detail page (<c>/events/{id}</c>).
/// Provides helpers to verify event title and ID are displayed correctly on the detail view.
/// Follows the same pattern as LoginPage with private locators, fluent interface, and assertion methods.
/// </summary>
public class EventDetailPage : BasePage
{
    public EventDetailPage(IWebDriver driver, WaitHelper wait) : base(driver, wait)
    {
    }

    // ── Page element locators ────────────────────────────────────────────────────
    private readonly By _eventSearchInput = By.XPath("//input[contains(@placeholder,'Search events')]");
    private readonly By _eventTitleHeading = By.XPath("//h1");
    private readonly By _eventIdDisplay = By.XPath("//*[contains(text(),'ID:')]");

    // ── Fluent helpers to wait for page load ─────────────────────────────────────
    public EventDetailPage WaitForPageLoad(TimeSpan? timeout = null)
    {
        Wait.WaitForElementVisible(_eventTitleHeading, timeout ?? TimeSpan.FromSeconds(10));
        return this;
    }

    // ── Navigation ───────────────────────────────────────────────────────────────
    /// <summary>
    /// Navigates to the event detail page by URL.
    /// </summary>
    public EventDetailPage NavigateToEventDetail(string appUrl, int eventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appUrl);
        if (eventId <= 0)
            throw new ArgumentException("Event ID must be positive", nameof(eventId));

        var detailUrl = $"{appUrl.TrimEnd('/')}/events/{eventId}";
        Driver.Navigate().GoToUrl(detailUrl);
        return this;
    }

    // ── Event list interactions ─────────────────────────────────────────────────
    public void SearchEventByTitle(string eventTitle)
    {
        if (string.IsNullOrWhiteSpace(eventTitle))
            throw new ArgumentException("Event title must be provided.", nameof(eventTitle));

        var searchElement = Wait.WaitForElementVisible(_eventSearchInput, TimeSpan.FromSeconds(10));
        searchElement.Clear();
        EnterText(_eventSearchInput, eventTitle);
    }

    public bool HasSearchInput()
    {
        return Driver.FindElements(_eventSearchInput).Count > 0;
    }

    public void SearchOrRefreshEventList(string eventTitle)
    {
        if (HasSearchInput())
        {
            SearchEventByTitle(eventTitle);
            return;
        }

        // Some builds fetch event data on page load without showing a search input.
        Driver.Navigate().Refresh();
        Wait.WaitForPageLoaded();
    }

    public bool IsEventVisibleInSearch(string eventTitle)
    {
        if (string.IsNullOrWhiteSpace(eventTitle))
            return false;

        return Driver.PageSource.Contains(eventTitle, StringComparison.OrdinalIgnoreCase);
    }

    public void ClickEventByTitle(string eventTitle)
    {
        if (string.IsNullOrWhiteSpace(eventTitle))
        {
            throw new ArgumentException("Event title must be provided.", nameof(eventTitle));
        }

        if (HasSearchInput())
        {
            SearchEventByTitle(eventTitle);
        }

        var exactEventTitle = By.XPath($"//*[@data-testid='event-card']//h3[normalize-space()=\"{eventTitle}\"]");
        var exactEventLink = By.XPath($"//*[@data-testid='event-card']//a[h3[normalize-space()=\"{eventTitle}\"]]");

        var titleVisible = Wait.WaitForElementVisible(exactEventTitle, TimeSpan.FromSeconds(10));
        if (titleVisible == null)
        {
            throw new InvalidOperationException($"Event '{eventTitle}' was not displayed in filtered search results.");
        }

        var link = Wait.WaitForElementVisible(exactEventLink, TimeSpan.FromSeconds(10));
        if (link == null)
        {
            throw new InvalidOperationException($"Event link for '{eventTitle}' was not visible.");
        }

        ((IJavaScriptExecutor)Driver).ExecuteScript("arguments[0].scrollIntoView({ block: 'center' });", link);
        try
        {
            link.Click();
        }
        catch (StaleElementReferenceException)
        {
            var retryLink = Wait.WaitForElementVisible(exactEventLink, TimeSpan.FromSeconds(5));
            if (retryLink == null)
            {
                throw;
            }

            ((IJavaScriptExecutor)Driver).ExecuteScript("arguments[0].scrollIntoView({ block: 'center' });", retryLink);
            retryLink.Click();
        }
        catch (ElementClickInterceptedException)
        {
            ((IJavaScriptExecutor)Driver).ExecuteScript("arguments[0].click();", link);
        }
    }

    // ── Verification methods ─────────────────────────────────────────────────────
    /// <summary>
    /// Verifies the event title is displayed on the detail page.
    /// </summary>
    public bool IsEventTitleDisplayed(string eventTitle)
    {
        if (string.IsNullOrWhiteSpace(eventTitle))
            return false;

        try
        {
            var titleSelector = By.XPath($"//h1[normalize-space()='{eventTitle}']");
            var element = Wait.WaitForElementVisible(titleSelector, TimeSpan.FromSeconds(5));
            return element != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Verifies the event ID is displayed on the detail page.
    /// Note: For EventHub UI, checks URL, data attributes, or visible text for the event ID.
    /// </summary>
    public bool IsEventIdDisplayed(int eventId)
    {
        try
        {
            // First, check if the URL contains the event ID (e.g., /events/37030)
            var currentUrl = Driver.Url;
            if (currentUrl.Contains($"/events/{eventId}"))
            {
                return true;
            }

            // Try to find visible text with event ID
            var idSelector = By.XPath($"//*[contains(text(),'ID:') and contains(text(),'{eventId}')]");
            var element = Wait.WaitForElementVisible(idSelector, TimeSpan.FromSeconds(2));
            if (element != null) return true;
            
            // Check data attributes
            var dataSelector = By.XPath($"//*[@data-event-id='{eventId}' or @data-id='{eventId}']");
            element = Wait.WaitForElementVisible(dataSelector, TimeSpan.FromSeconds(2));
            return element != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the displayed event title text from the detail page.
    /// </summary>
    public string GetDisplayedEventTitle()
    {
        try
        {
            var element = Wait.WaitForElementVisible(_eventTitleHeading, TimeSpan.FromSeconds(5));
            return element?.Text ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Gets the displayed event ID from the detail page (extracts from "ID: {id}" text).
    /// </summary>
    public string GetDisplayedEventId()
    {
        try
        {
            var element = Wait.WaitForElementVisible(_eventIdDisplay, TimeSpan.FromSeconds(5));
            if (element == null) return string.Empty;

            var text = element.Text;
            var parts = text.Split(new[] { "ID:" }, StringSplitOptions.None);
            return parts.Length > 1 ? parts[1].Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

}

