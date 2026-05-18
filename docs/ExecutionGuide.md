# Execution Guide

## 1. Run All Tests

```bash
dotnet test
```

## 2. Run API Tests Only

```bash
dotnet test tests/APITests/APITests.csproj
```

## 3. Run UI Tests Only

```bash
dotnet test tests/UITests/UITests.csproj
```

## 4. Run by Priority/Category

Examples:

```bash
dotnet test --filter "Category=High"
dotnet test --filter "Category=Smoke"
dotnet test --filter "Category=Sanity"
```

## 5. Run by Role

Bash:

```bash
TEST_EXECUTION_ROLE=user dotnet test tests/APITests/APITests.csproj
TEST_EXECUTION_ROLE=admin dotnet test tests/UITests/UITests.csproj
```

Windows PowerShell:

```powershell
$env:TEST_EXECUTION_ROLE = "user"
dotnet test tests/APITests/APITests.csproj

$env:TEST_EXECUTION_ROLE = "admin"
dotnet test tests/UITests/UITests.csproj
```

Role resolution order at runtime:

1. Method-level `[TestRole("role")]`
2. Class-level `[TestRole("role")]`
3. `TEST_EXECUTION_ROLE`

## 6. Cross-Browser UI Run

Bash:

```bash
TestSettings__Browser=chrome dotnet test tests/UITests/UITests.csproj
TestSettings__Browser=firefox dotnet test tests/UITests/UITests.csproj
TestSettings__Browser=edge dotnet test tests/UITests/UITests.csproj
```

Windows PowerShell:

```powershell
$env:TestSettings__Browser = "chrome"
dotnet test tests/UITests/UITests.csproj
```

## 7. Select Reporter

Default reporter is configured in `config/appsettings.json`:

```json
"Reporting": {
  "ActiveReporter": "Html"
}
```

Override per run:

```bash
Reporting__ActiveReporter=Allure dotnet test
```

```powershell
$env:Reporting__ActiveReporter = "Allure"
dotnet test
```

## 8. Output Locations

Primary report artifacts:

- `reports/execution-report.html`
- `reports/logs/`
- `reports/screenshots/`

Reporter-specific JSON/attachments are also written under `reports/`.

## 9. Common Issues

- No tests discovered: verify project path and target framework.
- UI driver startup fails: verify browser installation and `TestSettings__Browser` value.
- Role auth failures: verify `TEST_{ROLE}_EMAIL` and `TEST_{ROLE}_PASSWORD` for the role being executed.
- Missing report output: ensure `Reporting:ActiveReporter` is valid (`Html` or `Allure`) and test teardown completed.
