
# Git Workflow Guide

Project: SeleniumAutomationFramework  
Purpose: Define the Git workflow and collaboration process for contributors working on this repository.

Related guides:

- [SetupGuide.md](./SetupGuide.md)
- [ExecutionGuide.md](./ExecutionGuide.md)
- [AutomationCodingStandards.md](./AutomationCodingStandards.md)
- [FrameworkArchitecture.md](./FrameworkArchitecture.md)
- [README.md](../README.md)

---

# 1. Objectives

This document defines how contributors should use Git when working on this project to ensure:

- clean commit history
- stable main branch
- traceable changes
- smooth collaboration

---

# 2. Branch Strategy

This project follows a **simple feature branch workflow**.

Main branches:

- main → stable branch
- feature branches → development work

The **main branch should always remain stable and runnable**.

---

# 3. Creating a Feature Branch

All development must happen in feature branches.

Example:

```bash
git checkout -b feature/login-tests
```

Branch naming convention:

feature/<short-description>
fix/<short-description>
docs/<short-description>

Examples:

feature/login-page-object
feature/api-client
fix/login-timeout
docs/update-architecture-guide
```

Bug fixes:

```
fix/<short-description>
```

Examples:

```
fix/login-timeout
fix/flaky-test
```

---

# 4. Keeping Your Branch Updated

Before starting work each day:

```bash
git checkout main
git pull origin main
```

Then update your feature branch:

```bash
git checkout feature/my-feature
git merge main
```

This ensures your branch stays up to date.

---

# 5. Making Commits

Make small, meaningful commits.

Example:

```bash
git add .
git commit -m "Add LoginPage page object"
```

Commit message format:

```
<type>: <short description>
```

Examples:

```
feat: add login page object
feat: implement driver manager
fix: handle stale element exception
docs: update architecture guide
```

Commit types:

| Type | Meaning |
|-----|--------|
feat | New feature |
fix | Bug fix |
docs | Documentation |
refactor | Code refactoring |
test | Test improvements |

---

# 6. Pushing Changes

Push your branch to GitHub:

```bash
git push origin feature/my-feature
```

---

# 7. Pull Requests

After completing a feature:

1. Push the branch
2. Open a Pull Request
3. Request review

Pull requests should include:

- clear description of changes
- screenshots if UI behavior changed
- link to related issue (if applicable)

---

# 8. Code Review Guidelines

Reviewers should verify:

- Page Object pattern is respected
- No direct WebDriver calls in tests
- No Thread.Sleep usage
- Proper naming conventions
- No duplicated code
- Clean commit history

---

# 9. Merging Pull Requests

Once approved, the PR can be merged into `main`.

Use **Squash and Merge** to keep history clean.

Example commit message:

```
feat: implement login page object and tests
```

---

# 10. Deleting Feature Branches

After merging a feature branch:

```bash
git branch -d feature/my-feature
git push origin --delete feature/my-feature
```

This keeps the repository clean.

---

# 11. Handling Conflicts

If merge conflicts occur:

1. Pull the latest main branch
2. Resolve conflicts locally
3. Re-run tests
4. Commit resolved files

Example:

```bash
git merge main
```

Then resolve conflicts manually.

---

# 12. Best Practices

- Pull latest changes before starting work
- Keep branches small and focused
- Commit frequently
- Write meaningful commit messages
- Avoid committing build artifacts

---

# 13. Summary

Recommended workflow:

1. Create feature branch
2. Implement changes
3. Commit regularly
4. Push branch
5. Open pull request
6. Review and merge into main

Following this workflow ensures a stable and maintainable project history.
