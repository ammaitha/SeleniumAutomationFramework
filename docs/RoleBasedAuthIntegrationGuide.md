# Role-Based Auth Integration Guide

## Purpose

Document the current role-specific authentication model used by UI and API tests.

Related docs:

- `docs/ApiAuthTestSuiteBrief.md`
- `docs/TokenStorageAndExpiry.md`
- `docs/ExecutionGuide.md`

---

## 1. Role Resolution Order

Execution role selection:

1. Method-level `[TestRole("role")]`
2. Class-level `[TestRole("role")]`
3. `TEST_EXECUTION_ROLE` environment variable
4. Fail fast when auth-dependent test has no resolved role

This logic is implemented in `src/Framework.Reports/ReportTestBase.cs`.

---

## 2. Credential Resolution Order

For the resolved role, credentials are loaded in this order:

1. `TEST_{ROLE}_EMAIL` and `TEST_{ROLE}_PASSWORD`
2. `roles.{role}` values from `resources/testdata/loginData.json`
3. Fail fast with role-specific error

Implementation: `src/Framework.Data/RoleCredentialProvider.cs`.

---

## 3. Supported Role Patterns

### Pattern A: Role from environment

```powershell
$env:TEST_EXECUTION_ROLE = "user"
dotnet test tests/APITests/APITests.csproj
```

### Pattern B: Class-level role

```csharp
[TestRole("admin")]
public class AdminEventsTests : APITestBase
{
}
```

### Pattern C: Method-level override

```csharp
[Test]
[TestRole("viewer")]
public async Task ViewerCannotCreateEvent()
{
    // Method role overrides class/env role.
}
```

---

## 4. API Positive and Negative Auth Behavior

In `tests/APITests/ApiTestBase.cs`:

- Positive flows use suite-level token reuse per role.
- `_suiteTokens` caches token state by role.
- `EnsureSuiteTokenAsync()` refreshes expired role token under `_suiteLock`.
- Negative scenarios use explicit `LoginAsync(..., tokenScenario, tokenState: false)`.

This keeps positive tests fast and negative tests deterministic.

---

## 5. Test Data Shape

`resources/testdata/loginData.json` contains a `roles` object, for example:

```json
{
  "roles": {
    "admin": {
      "email": "${TEST_ADMIN_EMAIL:-}",
      "password": "${TEST_ADMIN_PASSWORD:-}"
    },
    "user": {
      "email": "${TEST_USER_EMAIL:-}",
      "password": "${TEST_USER_PASSWORD:-}"
    }
  }
}
```

The `:-` placeholder default keeps unused roles non-blocking.

---

## 6. Practical Guardrails

- Keep role names normalized (lowercase) in tests and data.
- Do not log raw passwords or bearer tokens.
- Resolve credentials only through `RoleCredentialProvider`/`RoleCredentialResolver`.
- Use role-scoped env vars in CI to make runs explicit and auditable.

---

## 7. Failure Messages You Should Expect

- Missing role declaration: indicates no role from method/class/env.
- Missing role credentials: indicates incomplete env vars and empty JSON fallback for that role.
- Unknown role: indicates role key is not present in provider.

These errors are intentional and should fail fast during setup.
