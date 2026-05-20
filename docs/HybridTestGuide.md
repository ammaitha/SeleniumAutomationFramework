# Hybrid Test Guide

Hybrid tests validate an end-to-end scenario that spans **three layers simultaneously**:

| Layer | What it validates |
|---|---|
| **API** | Business operation succeeds via HTTP (create, book, update, etc.) |
| **UI** | The result is reflected in the browser without a manual login |
| **Network** | The browser makes the expected backend calls with the correct responses |

---

## Framework Architecture

```
tests/HybridTests/
├── HybridBaseTest.cs              ← Shared login setup (all hybrid tests)
├── EventCreationHybridTests.cs    ← Event-specific hybrid test

src/Framework.Core/Utilities/
├── BrowserSessionBootstrapper.cs  ← Seeds browser storage / cookies from API token
├── NetworkHelper.cs               ← DevTools network capture + retry utility

src/Framework.API/
├── ApiSessionContext.cs           ← AsyncLocal token store shared across the test
├── AuthClient.cs                  ← Executes login POST, extracts token
├── APIClient.cs                   ← Base HTTP client wrapper
```

---

## How API Login Works

The hybrid base setup (`HybridBaseTest.SetUpHybridContext`) initialises an `APIClient` and an `AuthClient` from configuration. **No browser is involved at this stage.**

```csharp
// HybridBaseTest.cs [SetUp]
var apiBaseUrl = ConfigManager.GetString("Api:BaseUrl");
HybridApiClient = new APIClient(new HttpClient { BaseAddress = new Uri(apiBaseUrl) });
HybridAuthClient = new AuthClient(HybridApiClient, Logger, HybridLoginEndpoint, tokenJsonPath);
```

In the test itself, credentials are resolved from `loginData.json` via the `[TestRole]` attribute and a direct HTTP call is made:

```csharp
var loginResponse = HybridAuthClient.ExecuteLoginRequestAsync(email, password, CancellationToken.None)
    .GetAwaiter().GetResult();

var tokenState = HybridAuthClient.ExtractTokenFromResponse(loginResponseBody);
// tokenState.AccessToken  → JWT bearer token
// tokenState.ExpiresAt    → when the token expires
// tokenState.IsValid      → convenience bool
```

The extracted `tokenState` is immediately stored in the thread-scoped session context so any other API client in the same test can use it automatically:

```csharp
ApiSessionContext.Current.SetToken(tokenState);
```

---

## How the Session Is Stored

`ApiSessionContext` is backed by `AsyncLocal<T>`, which means **each test run has its own isolated token**, even in parallel execution:

```
Test A (Thread 1) ──→ ApiSessionContext.Current  ──→ TokenState { AccessToken = "ey..." }
Test B (Thread 2) ──→ ApiSessionContext.Current  ──→ TokenState { AccessToken = "ey..." }  (different token)
```

The token is **never written to disk or shared across tests**. It lives only for the lifetime of a single test execution context.

---

## How the API Session Is Used in the UI (Session Bootstrap)

After API login, the browser has no knowledge of the authenticated session. `BrowserSessionBootstrapper` bridges this gap by:

1. Navigating the browser to the application base URL (creates origin context).
2. Replaying `Set-Cookie` headers from the login API response as browser cookies.
3. Seeding `localStorage` and `sessionStorage` with the token under all common key names (`eventhub_token`, `token`, `accessToken`, etc.) plus the current user JSON.
4. Navigating to the application again so the frontend JavaScript picks up the seeded values.

```csharp
var sessionBootstrapper = new BrowserSessionBootstrapper(Driver);
sessionBootstrapper.BootstrapAuthenticatedSession(
    new Uri(ApplicationBaseUrl),
    tokenState.AccessToken!,
    currentUserJson,
    setCookieHeaders: loginResponse.Headers.TryGetValues("Set-Cookie", out var v) ? v : null,
    waitAfterNavigation: () => Wait.WaitForPageLoaded());
```

After this call the browser is authenticated — no login form is filled in, no UI credential flow occurs.

---

## How Network Responses Are Validated

`NetworkHelper` attaches to the Chrome DevTools Protocol before the action that triggers the network call, then asserts on what came back.

```csharp
using (var networkHelper = new NetworkHelper(Driver, Logger))
{
    // Trigger the UI action that should fire the API call
    eventDetailPage.ClickEventByTitle(eventTitle);
    Wait.WaitForPageLoaded();

    // Capture up to 3 responses whose URL contains "/api/events/"
    var responses = networkHelper.WaitForAllResponses("/api/events/", maxCount: 3, timeoutSeconds: 15)
        .GetAwaiter().GetResult();

    if (responses.Count > 0)
    {
        var last = responses[^1];
        var body = JObject.Parse(last.Body);

        Assert.Multiple(() =>
        {
            Assert.That(last.StatusCode, Is.EqualTo(200));
            Assert.That(body.SelectToken("data.id")?.Value<int>(), Is.EqualTo(expectedId));
            Assert.That(body.SelectToken("data.title")?.Value<string>(), Is.EqualTo(expectedTitle));
        });
    }
}
```

Key points:
- Wrap in `using` so DevTools subscription is disposed after the block.
- `WaitForAllResponses` does not throw if fewer than `maxCount` responses arrive — it stops at timeout.
- URL matching is a substring check (e.g. `"/api/events/"` matches `https://api.host.com/api/events/37193`).

---

## Retry on Transient Transport Failures

`NetworkHelper.ExecuteWithRetry` and `NetworkHelper.IsTransientTransportFailure` handle flaky HTTP transport errors without test-level boilerplate:

```csharp
var result = NetworkHelper.ExecuteWithRetry(
    () => HybridEventsApiClient.CreateEventAsync(payload).GetAwaiter().GetResult(),
    shouldRetry: NetworkHelper.IsTransientTransportFailure,
    onRetry: (ex, attempt, max) => Logger.Warning(ex, "Retrying {attempt}/{max}", attempt, max));
```

`IsTransientTransportFailure` returns `true` for `HttpRequestException` and connection-level `InvalidOperationException`, and recursively checks `InnerException`.

---

## Current Test: `EventCreationHybridTests`

File: [tests/HybridTests/EventCreationHybridTests.cs](../tests/HybridTests/EventCreationHybridTests.cs)

**Full flow:**

```
[SetUp] HybridBaseTest.SetUpHybridContext()
    └── Init APIClient + AuthClient from config/loginData.json

[Test] Event_CreatedViaAPI_Should_AppearInUI_And_DetailNetworkResponseShouldMatch()
    ├── InitializeEventApiClientFromTestData()    ← load eventData.json, build EventsApiClient
    ├── API login → extract token → store in ApiSessionContext
    ├── Fetch /me → build currentUserJson
    ├── BrowserSessionBootstrapper → seed browser storage → navigate to app
    ├── Build create event payload from eventData.json
    ├── NetworkHelper.ExecuteWithRetry → POST /api/events → assert 201 + success:true
    ├── Navigate to /events list
    │   ├── NetworkHelper capture /api/events → search UI
    │   └── Wait.UntilTrue(...) then assert event visible in list (8s when network capture is enabled, 20s otherwise)
    ├── Click event → NetworkHelper capture /api/events/{id}
    │   └── Assert status 200 + id + title match
    ├── Assert event detail page shows correct title and ID
    └── [finally] DELETE /api/events/{id}   ← cleanup always runs

[TearDown] HybridBaseTest.TearDownHybridContext()
    └── HybridApiClient.Dispose()
```

---

## Adding a New Hybrid Test

Below is a complete, runnable example: **book an event via API, verify the booking appears in the UI, and validate the network response.**

### Step 1 — Add a Booking API client initializer to `HybridBaseTest`

```csharp
// In HybridBaseTest.cs — add field
protected BookingsApiClient HybridBookingsApiClient = null!;

// Add new opt-in method (mirrors InitializeEventApiClientFromTestData)
protected void InitializeBookingsApiClientFromTestData()
{
    var bookingData = LoadTestData<JObject>("bookingData.json")!;
    var bookingEndpoints = new Framework.Contracts.EndpointData.BookingEndpointData
    {
        List     = bookingData["endpoints"]!["list"]!.Value<string>()!,
        Create   = bookingData["endpoints"]!["create"]!.Value<string>()!,
        GetById  = bookingData["endpoints"]!["getById"]!.Value<string>()!,
        CancelById = bookingData["endpoints"]!["cancelById"]!.Value<string>()!,
    };
    HybridBookingsApiClient = new BookingsApiClient(HybridApiClient, Logger, bookingEndpoints);
}
```

### Step 2 — Create the test file

```csharp
// tests/HybridTests/BookingHybridTests.cs
using NUnit.Framework;
using Framework.API;
using Framework.Core.Utilities;
using Framework.Reports;
using Newtonsoft.Json.Linq;
using UITests.Pages;

namespace HybridTests;

[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
[Parallelizable(ParallelScope.All)]
public class BookingHybridTests : HybridBaseTest
{
    [Test]
    [TestRole("user")]
    [Category("Hybrid")]
    [Priority(TestPriority.High)]
    public void Booking_CreatedViaAPI_Should_AppearInUI_And_NetworkResponseShouldMatch()
    {
        // ── Step 1: initialise domain-specific API clients ────────────────────
        InitializeEventApiClientFromTestData();       // needed to create a supporting event
        InitializeBookingsApiClientFromTestData();    // needed to create the booking

        // ── Step 2: API login + browser session bootstrap ────────────────────
        var role        = GetCurrentTestRole();
        var loginData   = LoadLoginData();
        var credentials = ResolveRoleCredentials(loginData, role);

        var loginResponse = HybridAuthClient
            .ExecuteLoginRequestAsync(credentials.Email, credentials.Password, CancellationToken.None)
            .GetAwaiter().GetResult();
        var loginBody  = loginResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        var tokenState = HybridAuthClient.ExtractTokenFromResponse(loginBody);

        Assert.Multiple(() =>
        {
            Assert.That(loginResponse.IsSuccessStatusCode, Is.True, "API login must succeed.");
            Assert.That(tokenState,        Is.Not.Null,  "Token state must be returned.");
            Assert.That(tokenState!.IsValid, Is.True,    "Token must be valid.");
        });

        ApiSessionContext.Current.SetToken(tokenState);

        var currentUserJson = ApiSessionContext.FetchCurrentUserJson(
            HybridApiClient, HybridMeEndpoint, tokenState!.AccessToken!);

        var bootstrapper = new BrowserSessionBootstrapper(Driver);
        bootstrapper.BootstrapAuthenticatedSession(
            new Uri(ApplicationBaseUrl),
            tokenState.AccessToken!,
            currentUserJson,
            setCookieHeaders: loginResponse.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : null,
            waitAfterNavigation: () => Wait.WaitForPageLoaded());

        // ── Step 3: create a supporting event via API ────────────────────────
        var eventData  = LoadTestData<JObject>("eventData.json")!;
        var eventTitle = $"Hybrid Booking Event {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

        var eventPayload = ((JObject)eventData["events"]!["createPayload"]!).DeepClone();
        eventPayload["title"]     = eventTitle;
        eventPayload["eventDate"] = DateTimeOffset.UtcNow.AddDays(30).ToString("O");

        var eventResult = NetworkHelper.ExecuteWithRetry(
            () => HybridEventsApiClient.CreateEventAsync(eventPayload).GetAwaiter().GetResult(),
            shouldRetry: NetworkHelper.IsTransientTransportFailure);

        Assert.That((int)eventResult.StatusCode, Is.EqualTo(201), "Supporting event must be created.");
        var eventId = int.Parse(
            JObject.Parse(eventResult.ResponseBody)
                   .SelectToken(eventData["assertions"]!["createdEventIdJsonPath"]!.Value<string>()!)!.ToString());

        // ── Step 4: book the event via API ───────────────────────────────────
        var bookingData    = LoadTestData<JObject>("bookingData.json")!;
        var bookingPayload = ((JObject)bookingData["bookings"]!["createPayload"]!).DeepClone();
        bookingPayload["eventId"] = eventId;

        var bookingResult = NetworkHelper.ExecuteWithRetry(
            () => HybridBookingsApiClient.CreateBookingAsync(bookingPayload).GetAwaiter().GetResult(),
            shouldRetry: NetworkHelper.IsTransientTransportFailure);

        Assert.That((int)bookingResult.StatusCode, Is.EqualTo(201), "Booking API must return 201.");
        var bookingIdPath = bookingData["assertions"]!["createdBookingIdJsonPath"]!.Value<string>()!;
        var bookingId = int.Parse(
            JObject.Parse(bookingResult.ResponseBody).SelectToken(bookingIdPath)!.ToString());

        Logger.Information("[Hybrid] Booking created: id={BookingId} for event id={EventId}", bookingId, eventId);

        // ── Step 5: verify booking appears in UI + validate network ──────────
        int capturedBookingId = 0;
        try
        {
            // Navigate to bookings list
            Driver.Navigate().GoToUrl(new Uri(new Uri(ApplicationBaseUrl), "/bookings").ToString());
            Wait.WaitForPageLoaded();

            // TODO: replace BookingListPage with your actual page object
            // var bookingListPage = new BookingListPage(Driver, Wait);

            // Capture the list API call while the page loads
            using (var networkHelper = new NetworkHelper(Driver, Logger))
            {
                // bookingListPage.SearchBookingById(bookingId);   ← add your search/filter action here

                var listResponses = networkHelper
                    .WaitForAllResponses("/api/bookings", maxCount: 2, timeoutSeconds: 10)
                    .GetAwaiter().GetResult();
                Logger.Information("[Hybrid] Booking list network responses: {Count}", listResponses.Count);
            }

            // Assert.That(bookingListPage.IsBookingVisible(bookingId), Is.True, "Booking must appear in UI.");

            // Navigate to booking detail and validate network response
            using (var networkHelper = new NetworkHelper(Driver, Logger))
            {
                // bookingListPage.ClickBookingById(bookingId);
                Wait.WaitForPageLoaded();

                var detailResponses = networkHelper
                    .WaitForAllResponses($"/api/bookings/{bookingId}", maxCount: 2, timeoutSeconds: 10)
                    .GetAwaiter().GetResult();

                if (detailResponses.Count > 0)
                {
                    var last = detailResponses[^1];
                    var body = JObject.Parse(last.Body);
                    capturedBookingId = body.SelectToken("data.id")?.Value<int>() ?? 0;

                    Assert.Multiple(() =>
                    {
                        Assert.That(last.StatusCode,     Is.EqualTo(200), "Booking detail network response must be 200.");
                        Assert.That(capturedBookingId,   Is.EqualTo(bookingId), "Network booking id must match.");
                    });
                }
            }
        }
        finally
        {
            // ── Step 6: cleanup — cancel booking then delete event ────────────
            if (bookingId > 0)
            {
                try
                {
                    HybridBookingsApiClient.CancelBookingAsync(bookingId).GetAwaiter().GetResult();
                    Logger.Information("[Hybrid] Cleanup: booking id={BookingId} cancelled.", bookingId);
                }
                catch { /* ignore cleanup errors */ }
            }

            if (eventId > 0)
            {
                try
                {
                    HybridEventsApiClient.DeleteEventAsync(eventId).GetAwaiter().GetResult();
                    Logger.Information("[Hybrid] Cleanup: event id={EventId} deleted.", eventId);
                }
                catch { /* ignore cleanup errors */ }
            }
        }
    }
}
```

### Step 3 — Add the test to the project (if needed)

If the file doesn't auto-include, add to `HybridTests.csproj`:
```xml
<Compile Include="BookingHybridTests.cs" />
```

### Step 4 — Run it

```bash
dotnet test tests/HybridTests/HybridTests.csproj \
  --filter "FullyQualifiedName~Booking_CreatedViaAPI_Should_AppearInUI"
```

---

## Three-Layer Assertion Checklist

When writing any hybrid test, verify all three layers:

| Layer | How to assert |
|---|---|
| **API** | `Assert.That((int)result.StatusCode, Is.EqualTo(201))` |
| **API body** | `JObject.Parse(result.ResponseBody).SelectToken("data.id")` |
| **UI visibility** | `Assert.That(page.IsItemVisible(id), Is.True)` |
| **Network status** | `Assert.That(networkResponse.StatusCode, Is.EqualTo(200))` |
| **Network body** | `Assert.That(body.SelectToken("data.title")?.Value<string>(), Is.EqualTo(expected))` |

---

## Key Framework Classes

| Class | Location | Responsibility |
|---|---|---|
| `HybridBaseTest` | `tests/HybridTests/HybridBaseTest.cs` | Login setup; opt-in API client initializers; dispose teardown |
| `BrowserSessionBootstrapper` | `src/Framework.Core/Utilities/BrowserSessionBootstrapper.cs` | Seeds browser localStorage/sessionStorage/cookies from API token |
| `ApiSessionContext` | `src/Framework.API/ApiSessionContext.cs` | AsyncLocal token store; `FetchCurrentUserJson` |
| `NetworkHelper` | `src/Framework.Core/Utilities/NetworkHelper.cs` | DevTools capture; `ExecuteWithRetry`; `IsTransientTransportFailure` |
| `AuthClient` | `src/Framework.API/AuthClient.cs` | Posts login, extracts `TokenState` |
| `APIClient` | `src/Framework.API/ApiClient.cs` | Base HTTP client; bearer injection; response wrapping |

---

## Configuration Reference

| Key (`appsettings.json`) | Purpose |
|---|---|
| `Api:BaseUrl` | Base URL of the backend API |
| `Api:ShowBearerToken` | `false` = bearer token is masked in logs |
| `TestSettings:BaseUrl` | Application base URL for browser navigation |
| `TestSettings:Browser` | `chrome` (must support DevTools) |
