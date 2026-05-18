# Reporter Integration Guide

This guide explains how to add, remove, or replace a reporter in SeleniumAutomationFramework.

## 1. Reporter Architecture

Core components:

- `src/Framework.Reports/IReporter.cs` - reporter contract
- `src/Framework.Reports/ReporterAliasAttribute.cs` - alias metadata
- `src/Framework.Reports/ReporterFactory.cs` - reporter discovery/creation
- `src/Framework.Reports/ReportManager.cs` - lifecycle and safe dispatch
- `src/Framework.Reports/SessionManager.cs` - session boundary and cleanup
- `config/appsettings.json` - active reporter setting

Built-in reporter implementations:

- `src/Framework.Reports/Reporters/HtmlReporter.cs`
- `src/Framework.Reports/Reporters/AllureReporter.cs`

## 2. Important Lifecycle Rules

- Configure reporter via `Reporting:ActiveReporter` (or `Reporting__ActiveReporter`).
- Keep global cleanup session-owned (`SessionManager`).
- Do not perform run-wide cleanup inside individual reporters during `InitializeSuite`.
- Use `ReportManager.RecordTestResult(...)` for centralized failure enrichment and attachment handling.

## 3. Add a New Reporter

1. Create a class under `src/Framework.Reports/Reporters/` implementing `IReporter`.
2. Add `[ReporterAlias("YourAlias")]` to the class.
3. Implement the full lifecycle methods required by `IReporter`.
4. Set the reporter in config:

```json
{
  "Reporting": {
    "ActiveReporter": "YourAlias"
  }
}
```

No manual registration is needed; discovery is reflection-based in `ReporterFactory`.

## 4. Switch Reporters

Set the active reporter name:

```json
"Reporting": {
  "ActiveReporter": "Html"
}
```

or

```json
"Reporting": {
  "ActiveReporter": "Allure"
}
```

You can also override for one run:

```bash
Reporting__ActiveReporter=Allure dotnet test
```

## 5. Remove a Reporter

1. Delete the reporter file from `src/Framework.Reports/Reporters/`.
2. Ensure no direct references remain in custom code.
3. Update `Reporting:ActiveReporter` to a valid remaining reporter.
4. Run tests and confirm fallback behavior/logging is acceptable.

## 6. Troubleshooting

- Reporter not found: check alias spelling and class attribute.
- Wrong reporter selected: verify env var override is not forcing another value.
- Missing artifacts: verify tests reached teardown and `ReportManager.FinalizeSuite()` path ran.
- Unexpected report resets: verify cleanup is not duplicated outside `SessionManager`.
