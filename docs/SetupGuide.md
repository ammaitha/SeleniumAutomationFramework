# Setup Guide

## 1. Prerequisites

Install the following tools:

- .NET SDK 10.x
- Git
- Visual Studio Code (recommended)
- At least one supported browser: Chrome, Firefox, or Edge

## 2. Clone, Restore, and Build

```bash
git clone <repo-url>
cd SeleniumAutomationFramework
dotnet restore
dotnet build
```

## 3. Configure Test Credentials

Use environment variables for secrets.

Recommended role-specific variables:

- `TEST_USER_EMAIL` / `TEST_USER_PASSWORD`
- `TEST_ADMIN_EMAIL` / `TEST_ADMIN_PASSWORD`
- `TEST_ORGANIZER_EMAIL` / `TEST_ORGANIZER_PASSWORD`
- `TEST_VIEWER_EMAIL` / `TEST_VIEWER_PASSWORD`

Notes:

- Credentials are validated lazily for the role being executed.
- You do not need to define every role unless that role is part of the current test run.

## 4. Configure Runtime Settings

Edit `config/appsettings.json` as needed:

- `TestSettings:Browser`
- `TestSettings:Headless`
- `TestSettings:ExplicitWaitSeconds`
- `TestSettings:BaseUrl`
- `TestSettings:AppUrl`
- `Api:BaseUrl`
- `Reporting:ActiveReporter`

Default reporter is `Html`.

## 5. Verify Installation

Run a smoke/sanity command:

```bash
dotnet test --filter "Category=Smoke"
```

## 6. Report Output

Framework outputs:

- `reports/execution-report.html`
- `reports/logs/`
- `reports/screenshots/`

## 7. Troubleshooting

- If build fails: run `dotnet restore`, then `dotnet build`.
- If browser launch fails: verify browser installation and `TestSettings__Browser` override.
- If role auth fails: verify that role's `TEST_{ROLE}_EMAIL` and `TEST_{ROLE}_PASSWORD` values in the same shell session.
- If report is missing: verify `Reporting:ActiveReporter` and ensure tests reached teardown.
