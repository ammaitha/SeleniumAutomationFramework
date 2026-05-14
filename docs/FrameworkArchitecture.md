# Selenium Automation Framework Architecture

## 1. Objective

Provide a reusable automation platform for UI and API testing with:

- NUnit-based execution
- Selenium Page Object Model for UI
- HttpClient-based API clients
- Role-aware authentication
- Pluggable reporting
- CI-friendly outputs and artifacts

---

## 2. High-Level Design

Execution flow:

Test classes -> shared test base -> framework services -> browser/API layers -> reports

The framework favors shared helpers and centralized lifecycle management so tests remain small and intent-focused.

---

## 3. Repository Structure

```text
SeleniumAutomationFramework
|- src/
|  |- Framework.Core/      # config, driver lifecycle, waits, logging, screenshots
|  |- Framework.API/       # HTTP abstractions, auth context, response validation
|  |- Framework.Data/      # JSON/Excel data providers + role credential resolver
|  |- Framework.Reports/   # report pipeline + reporter implementations
|  |- ApiClients/          # domain API clients (Auth, Events, Bookings)
|  |- Pages/               # shared page objects
|- tests/
|  |- UITests/
|  |- APITests/
|- config/
|  |- appsettings.json
|- resources/testdata/
|- reports/
|- ci/
|- docs/
```

---

## 4. Core Layer Details

### Framework.Core

- `ConfigManager` loads settings via standard .NET configuration precedence.
- `DriverManager` manages browser lifecycle and supports environment override via `TestSettings__Browser`.
- `WaitHelper` centralizes explicit wait behavior.
- `TestLogger` writes execution logs under `reports/logs`.
- `ScreenshotHelper` stores UI failure screenshots under `reports/screenshots`.

### Framework.API and ApiClients

- `APIClient` is the shared HTTP transport abstraction.
- `AuthClient` controls login and token scenario behavior (`valid`, `invalid`, `expired`, `missing`).
- `ApiSessionContext` keeps token/credentials in-memory per async flow.
- `AuthApiClient`, `EventsApiClient`, and `BookingsApiClient` expose feature-level endpoints.

### Framework.Data

- `JsonDataProvider` and `ExcelDataProvider` load test data.
- `RoleCredentialProvider` resolves credentials by role.
- Resolution order: `TEST_{ROLE}_EMAIL/PASSWORD` -> `roles.{role}` in JSON -> fail fast.

### Framework.Reports

- `ReportManager` is the single orchestration entry point.
- `ReporterFactory` discovers reporters by `ReporterAliasAttribute`.
- `SessionManager` owns run/session cleanup boundaries.
- Built-in reporters:
  - `HtmlReporter`
  - `AllureReporter`

---

## 5. Test Layer

### UI tests (`tests/UITests`)

- Inherit from `BaseTest`.
- Use page objects from `src/Pages`.
- Browser/session lifecycle is handled in setup/teardown.
- On failure, screenshot and page source are captured and attached to reporting.

### API tests (`tests/APITests`)

- Inherit from `APITestBase`.
- Role-specific suite token cache is maintained in-memory.
- Positive flows reuse suite token; expired tokens are refreshed under lock.
- Negative flows deliberately force token states and avoid silent auto-healing.

---

## 6. Role-Based Execution Model

Role resolution priority:

1. Method-level `TestRoleAttribute`
2. Class-level `TestRoleAttribute`
3. `TEST_EXECUTION_ROLE` environment variable
4. Fail if role is required but missing

This allows the same test implementation to run across user/admin/organizer/viewer credentials without code duplication.

---

## 7. Reporting Lifecycle

- Active reporter is selected by `Reporting:ActiveReporter` (or `Reporting__ActiveReporter`).
- Session cleanup is centrally owned by `SessionManager`; reporters should not perform global cleanup in `InitializeSuite`.
- `ReportManager.RecordTestResult` enriches failures with:
  - failure details (message + stack trace)
  - UI screenshot attachment when available

Default output root:

- `reports/execution-report.html`
- `reports/logs/`
- `reports/screenshots/`

Allure artifacts are produced in `reports/` as JSON result/attachment files used by the Allure-style HTML generation flow.

---

## 8. Configuration

Primary runtime config is in `config/appsettings.json`:

- `TestSettings` for UI/browser/waits/base URLs
- `Api` for API base URL and token masking behavior
- `Reporting` for active reporter and history setting

Environment variables can override configuration values using double-underscore notation.

---

## 9. CI/CD Fit

The framework is designed for pipeline execution with:

- deterministic CLI commands (`dotnet test ...`)
- role-scoped runs via environment variables
- artifact-friendly report/log/screenshot outputs

See:

- `docs/SetupGuide.md`
- `docs/ExecutionGuide.md`
- `docs/RoleBasedAuthIntegrationGuide.md`
- `docs/ReporterIntegrationGuide.md`
