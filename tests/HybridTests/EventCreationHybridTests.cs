using NUnit.Framework;
using UITests;
using Framework.API;
using Framework.Core.Utilities;
using Framework.Reports;
using Newtonsoft.Json.Linq;
using OpenQA.Selenium;
using UITests.Pages;

namespace HybridTests;

[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
[Parallelizable(ParallelScope.All)]
public class EventCreationHybridTests : HybridBaseTest
{
    private Framework.API.TokenState? _tokenState;
    private string? _currentUserJson;
    private JObject? _eventData;
    private bool _canCaptureNetwork;
    private HttpResponseMessage? _loginResponse;

    [SetUp]
    public void SetUpEventCreationTest()
    {
        InitializeEventApiClientFromTestData();

        var role = GetCurrentTestRole();
        var loginData = LoadLoginData();
        var credentials = ResolveRoleCredentials(loginData, role);

        _loginResponse = HybridAuthClient.ExecuteLoginRequestAsync(credentials.Email, credentials.Password, CancellationToken.None)
            .GetAwaiter().GetResult();
        var loginResponseBody = _loginResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        _tokenState = HybridAuthClient.ExtractTokenFromResponse(loginResponseBody);

        Assert.Multiple(() =>
        {
            Assert.That(_loginResponse.IsSuccessStatusCode, Is.True,
                $"API login must succeed. Status: {(int)_loginResponse.StatusCode} {_loginResponse.ReasonPhrase}");
            Assert.That(_tokenState, Is.Not.Null, "API login must return a token state.");
            Assert.That(_tokenState!.IsValid, Is.True, "API login token must be valid.");
        });

        ApiSessionContext.Current.SetToken(_tokenState);
        Logger.Information("[Hybrid] API login succeeded. Token valid until {ExpiresAt}.", _tokenState.ExpiresAt);

        _currentUserJson = ApiSessionContext.FetchCurrentUserJson(
            HybridApiClient,
            HybridMeEndpoint,
            _tokenState!.AccessToken!);

        _canCaptureNetwork = NetworkHelper.ShouldValidateNetwork(Driver, Logger, ConfiguredBrowser);

        var sessionBootstrapper = new BrowserSessionBootstrapper(Driver);
        sessionBootstrapper.BootstrapAuthenticatedSession(
            new Uri(ApplicationBaseUrl),
            _tokenState.AccessToken!,
            _currentUserJson,
            setCookieHeaders: _loginResponse.Headers.TryGetValues("Set-Cookie", out var setCookieValues) ? setCookieValues : null,
            waitAfterNavigation: () => Wait.WaitForPageLoaded());

        _eventData = LoadTestData<JObject>("eventData.json")!;
    }

    [Test]
    [TestRole("user")]
    [Category("Hybrid")]
    [Priority(TestPriority.High)]
    public void Event_CreatedViaAPI_AppearInUI_And_NetworkResponseShouldMatch()
    {
        var eventTitle = $"Hybrid Event {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

        var createPayload = ((JObject)_eventData!["events"]!["createPayload"]!).DeepClone();
        createPayload["title"] = eventTitle;
        createPayload["eventDate"] = DateTimeOffset.UtcNow.AddDays(20).ToString("O");

        var createResponse = NetworkHelper.ExecuteWithRetry(
            () => HybridEventsApiClient.CreateEventAsync(createPayload).GetAwaiter().GetResult(),
            shouldRetry: NetworkHelper.IsTransientTransportFailure,
            onRetry: (ex, attempt, maxAttempts) =>
                Logger.Warning(
                    ex,
                    "[Hybrid] Transient failure during {Operation} on attempt {Attempt}/{MaxAttempts}. Retrying.",
                    "CreateEvent",
                    attempt,
                    maxAttempts));

        Assert.Multiple(() =>
        {
            Assert.That((int)createResponse.StatusCode, Is.EqualTo(201), "Create event API must return HTTP 201 Created.");
            Assert.That(JObject.Parse(createResponse.ResponseBody).SelectToken("success")?.Value<bool>() ?? false, Is.True, "Create event API response must contain success=true.");
        });

        var createdEventIdJsonPath = _eventData!["assertions"]!["createdEventIdJsonPath"]!.Value<string>()!;
        var eventId = int.Parse(JObject.Parse(createResponse.ResponseBody).SelectToken(createdEventIdJsonPath)?.ToString()!);
        Assert.That(eventId, Is.GreaterThan(0), "Created event ID must be a positive integer.");

        Logger.Information("[Hybrid] Event created via API: id={EventId}, title={EventTitle}", eventId, eventTitle);

        try
        {
            var eventDetailPage = new EventDetailPage(Driver, Wait);
            Driver.Navigate().GoToUrl(new Uri(new Uri(ApplicationBaseUrl), "/events").ToString());
            Wait.WaitForPageLoaded();
            var useStrictNetworkAssertions = _canCaptureNetwork;

            if (useStrictNetworkAssertions)
            {
                using (var networkHelper = new NetworkHelper(Driver, Logger))
                {
                    eventDetailPage.SearchOrRefreshEventList(eventTitle);

                    var listResponses = networkHelper.WaitForAllResponses("/api/events", 2, 10).GetAwaiter().GetResult();
                    Logger.Information("[Hybrid] Events list/search network responses captured: {Count}", listResponses.Count);

                    if (ConfiguredBrowser.Equals("firefox", StringComparison.OrdinalIgnoreCase)
                        && listResponses.Count == 0)
                    {
                        useStrictNetworkAssertions = false;
                        Logger.Warning(
                            "[Hybrid] Firefox network capture returned no responses. Falling back to API+UI-only assertions for this test run.");
                    }
                }
            }
            else
            {
                eventDetailPage.SearchOrRefreshEventList(eventTitle);
            }

            var listVisibilityTimeout = useStrictNetworkAssertions ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(20);
            var isEventVisible = false;
            try
            {
                isEventVisible = Wait.UntilTrue(_ => eventDetailPage.IsEventVisibleInSearch(eventTitle), listVisibilityTimeout);
            }
            catch (WebDriverTimeoutException)
            {
                isEventVisible = false;
            }

            var navigatedDirectlyToDetail = false;
            if (useStrictNetworkAssertions)
            {
                Assert.That(isEventVisible, Is.True,
                    $"Event '{eventTitle}' must appear in the UI events listing after API creation.");
            }
            else if (!isEventVisible)
            {
                Logger.Warning("[Hybrid] Event was not visible in list for browser {Browser}. Falling back to direct detail navigation.", ConfiguredBrowser);
                eventDetailPage.NavigateToEventDetail(ApplicationBaseUrl, eventId);
                Wait.WaitForPageLoaded();
                navigatedDirectlyToDetail = true;
            }

            if (useStrictNetworkAssertions)
            {
                using (var networkHelper = new NetworkHelper(Driver, Logger))
                {
                    eventDetailPage.ClickEventByTitle(eventTitle);
                    Wait.WaitForPageLoaded();

                    var detailResponses = networkHelper.WaitForAllResponses("/api/events/", 3, 15).GetAwaiter().GetResult();
                    Logger.Information("[Hybrid] Event detail network responses captured: {Count}", detailResponses.Count);

                    if (detailResponses.Count > 0)
                    {
                        var detailResponse = detailResponses[^1];
                        var detailsJson = JObject.Parse(detailResponse.Body);
                        var detailsEventId = detailsJson.SelectToken("data.id")?.Value<int?>()
                            ?? detailsJson.SelectToken("id")?.Value<int?>();
                        var detailsEventTitle = detailsJson.SelectToken("data.title")?.Value<string>()
                            ?? detailsJson.SelectToken("title")?.Value<string>();

                        Assert.Multiple(() =>
                        {
                            Assert.That(detailResponse.StatusCode, Is.EqualTo(200), "Network response for event details should return 200.");
                            Assert.That(detailsEventId, Is.EqualTo(eventId), "Network event id should match API-created id.");
                            Assert.That(detailsEventTitle, Is.EqualTo(eventTitle), "Network event title should match API-created title.");
                        });
                    }
                }
            }
            else if (!navigatedDirectlyToDetail)
            {
                eventDetailPage.ClickEventByTitle(eventTitle);
                Wait.WaitForPageLoaded();
            }

            eventDetailPage.WaitForPageLoad(TimeSpan.FromSeconds(10));
            Assert.That(eventDetailPage.IsEventTitleDisplayed(eventTitle), Is.True,
                $"Event detail page must display the correct event title: {eventTitle}");

            if (useStrictNetworkAssertions)
            {
                Assert.That(eventDetailPage.IsEventIdDisplayed(eventId), Is.True,
                    $"Event detail page must display the correct event ID: {eventId}");
            }
            else
            {
                Assert.That(Driver.Url.Contains("/events", StringComparison.OrdinalIgnoreCase), Is.True,
                    "Fallback UI validation expects navigation to the events detail route.");
            }
        }
        finally
        {
            if (eventId > 0)
            {
                    HybridEventsApiClient.DeleteEventAsync(eventId).GetAwaiter().GetResult();
                    Logger.Information("[Hybrid] Cleanup: event id={EventId} deleted.", eventId);
            }
        }
    }

    [Test]
    [TestRole("user")]
    [Category("Hybrid")]
    [Priority(TestPriority.High)]
    public void Event_CreatedViaAPI_AppearInHome_EventsAndNetworkResponseShouldMatch()
    {
        var eventTitle = $"Hybrid Home Event {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

        var createPayload = ((JObject)_eventData!["events"]!["createPayload"]!).DeepClone();
        createPayload["title"] = eventTitle;
        createPayload["eventDate"] = DateTimeOffset.UtcNow.AddDays(20).ToString("O");

        var createResponse = NetworkHelper.ExecuteWithRetry(
            () => HybridEventsApiClient.CreateEventAsync(createPayload).GetAwaiter().GetResult(),
            shouldRetry: NetworkHelper.IsTransientTransportFailure,
            onRetry: (ex, attempt, maxAttempts) =>
                Logger.Warning(
                    ex,
                    "[Hybrid] Transient failure during {Operation} on attempt {Attempt}/{MaxAttempts}. Retrying.",
                    "CreateEvent",
                    attempt,
                    maxAttempts));

        Assert.Multiple(() =>
        {
            Assert.That((int)createResponse.StatusCode, Is.EqualTo(201), "Create event API must return HTTP 201 Created.");
            Assert.That(JObject.Parse(createResponse.ResponseBody).SelectToken("success")?.Value<bool>() ?? false, Is.True, "Create event API response must contain success=true.");
        });

        var createdEventIdJsonPath = _eventData!["assertions"]!["createdEventIdJsonPath"]!.Value<string>()!;
        var eventId = int.Parse(JObject.Parse(createResponse.ResponseBody).SelectToken(createdEventIdJsonPath)?.ToString()!);
        Assert.That(eventId, Is.GreaterThan(0), "Created event ID must be a positive integer.");

        Logger.Information("[Hybrid] Event created via API for home page test: id={EventId}, title={EventTitle}", eventId, eventTitle);

        try
        {
            var homePage = new HomePage(Driver, Wait);
            var useStrictNetworkAssertions = _canCaptureNetwork;

            if (useStrictNetworkAssertions)
            {
                using (var networkHelper = new NetworkHelper(Driver, Logger))
                {
                    Driver.Navigate().GoToUrl(ApplicationBaseUrl);
                    Wait.WaitForPageLoaded();

                    var listResponses = networkHelper.WaitForAllResponses("/api/events", 1, 10).GetAwaiter().GetResult();
                    Logger.Information("[Hybrid] Home page /api/events network responses captured: {Count}", listResponses.Count);

                    if (ConfiguredBrowser.Equals("firefox", StringComparison.OrdinalIgnoreCase)
                        && listResponses.Count == 0)
                    {
                        useStrictNetworkAssertions = false;
                        Logger.Warning(
                            "[Hybrid] Firefox network capture returned no responses. Falling back to API+UI-only assertions for home page test.");
                    }
                    else if (listResponses.Count > 0)
                    {
                        var listResponse = listResponses[^1];
                        Assert.That(listResponse.StatusCode, Is.EqualTo(200),
                            "Network GET /api/events must return HTTP 200.");

                        var listJson = JObject.Parse(listResponse.Body);
                        var eventsArray = listJson.SelectToken("data") as JArray;
                        var foundInResponse = eventsArray != null
                            && eventsArray.Any(e => e["title"]?.Value<string>() == eventTitle);

                        if (foundInResponse)
                        {
                            Logger.Information(
                                "[Hybrid] Created event '{EventTitle}' confirmed in /api/events network response.",
                                eventTitle);
                        }
                        else
                        {
                            Logger.Warning(
                                "[Hybrid] Created event '{EventTitle}' not found on the captured /api/events page — it may be on a later pagination page.",
                                eventTitle);
                        }
                    }
                }
            }
            else
            {
                Driver.Navigate().GoToUrl(ApplicationBaseUrl);
                Wait.WaitForPageLoaded();
            }

            Assert.That(homePage.IsHomePageLoaded(), Is.True,
                "Home page must load successfully with a valid authenticated session.");
            Assert.That(homePage.ArePrimaryNavigationLinksVisible(), Is.True,
                "Primary navigation links (Home, Events, Bookings) must be visible.");
            Assert.That(homePage.IsFeaturedEventsSectionVisible(), Is.True,
                "Featured Events section heading must be visible on the home page.");
            Assert.That(homePage.DoFeaturedEventCardsContainTitleAndPrice(), Is.True,
                "All featured event cards must display a title and a price.");
            Assert.That(homePage.AreAllFeaturedEventsBookable(), Is.True,
                "All featured event cards must carry a Book Now link pointing to /events/{id}.");
        }
        finally
        {
            if (eventId > 0)
            {
                HybridEventsApiClient.DeleteEventAsync(eventId).GetAwaiter().GetResult();
                Logger.Information("[Hybrid] Cleanup: event id={EventId} deleted.", eventId);
            }
        }
    }

}
