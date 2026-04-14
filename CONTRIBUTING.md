# Contributing to FANZi

Thank you for taking the time to contribute to FANZi! The following guidelines will help you get started quickly and ensure a smooth review process.

---

## Table of Contents

- [Code of Conduct](#code-of-conduct)
- [Getting Started](#getting-started)
- [How to Report a Bug](#how-to-report-a-bug)
- [How to Request a Feature](#how-to-request-a-feature)
- [Development Workflow](#development-workflow)
- [Coding Standards](#coding-standards)
- [Commit Message Conventions](#commit-message-conventions)
- [Pull Request Guidelines](#pull-request-guidelines)
- [Project Architecture](#project-architecture)

---

## Code of Conduct

This project follows the [Contributor Covenant Code of Conduct](https://www.contributor-covenant.org/version/2/1/code_of_conduct/). By participating you agree to abide by its terms. Please be respectful and constructive in all interactions.

---

## Getting Started

### Prerequisites

| Tool | Version | Notes |
|---|---|---|
| [.NET SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | 10.0+ | Required to build |
| IDE | Any | Visual Studio 2022+, JetBrains Rider, or VS Code |
| [OpenRGB](https://openrgb.org) | Latest | *Optional* — needed for RGB testing |
| Windows 10/11 | — | Required for hardware sensor testing |

### Fork and Clone

```bash
# Fork via GitHub UI, then:
git clone https://github.com/<your-username>/FANZi.git
cd FANZi
```

### Build

```bash
dotnet build src/Fanzi.FanControl/Fanzi.FanControl.csproj
```

### Run

```bash
dotnet run --project src/Fanzi.FanControl/Fanzi.FanControl.csproj --configuration Debug
```

---

## How to Report a Bug

1. **Search existing issues** to avoid duplicates.
2. Use the **Bug Report** issue template.
3. Include:
   - Windows version and architecture
   - .NET runtime version (`dotnet --version`)
   - Steps to reproduce
   - Expected vs actual behaviour
   - Relevant log output or screenshots

---

## How to Request a Feature

1. **Search existing issues** and discussions.
2. Use the **Feature Request** issue template.
3. Describe the use-case and the proposed solution clearly.

---

## Development Workflow

1. Create a branch from `main`:
   ```bash
   git checkout -b feat/my-feature
   # or
   git checkout -b fix/issue-42
   ```
2. Make your changes (see [Coding Standards](#coding-standards)).
3. Build and verify:
   ```bash
   dotnet build src/Fanzi.FanControl/Fanzi.FanControl.csproj
   dotnet run  --project src/Fanzi.FanControl/Fanzi.FanControl.csproj
   ```
4. Commit following the [convention](#commit-message-conventions).
5. Push your branch and open a Pull Request.

---

## Coding Standards

- **Language version:** C# 13 (latest features available in .NET 10).
- **Nullable reference types** are enabled — do not suppress warnings blindly.
- **MVVM pattern:** business logic belongs in `Services`; UI state belongs in `ViewModels`; views must contain no logic.
- **Naming conventions:**
  - Types: `PascalCase`
  - Private fields: `_camelCase`
  - Local variables and parameters: `camelCase`
  - Interfaces: `IPrefix`
- **Formatting:** use 4-space indentation; keep lines ≤ 120 characters where practical.
- **Comments:** XML doc comments (`///`) on all public members; inline comments for non-obvious logic only.
- **No magic numbers or strings:** use named constants or enum values.
- Keep methods short and focused on a single responsibility.

---

## Commit Message Conventions

FANZi uses [Conventional Commits](https://www.conventionalcommits.org/):

```
<type>(<scope>): <short summary>

[optional body]

[optional footer: BREAKING CHANGE or issue refs]
```

### Types

| Type | When to use |
|---|---|
| `feat` | New feature |
| `fix` | Bug fix |
| `docs` | Documentation only |
| `style` | Formatting, whitespace (no logic change) |
| `refactor` | Code restructure (no feature / bug change) |
| `perf` | Performance improvement |
| `test` | Add or fix tests |
| `chore` | Build, tooling, dependency updates |

### Examples

```
feat(rgb): add Cyberpunk theme preset
fix(hardware): handle null CPU sensor on non-admin run
docs(readme): add RGB connection instructions
chore(deps): upgrade Avalonia to 11.3.12
```

---

## Pull Request Guidelines

- Target the `main` branch.
- Give the PR a clear, descriptive title using the commit convention above.
- Fill in the pull request template completely.
- Keep PRs focused — one feature or bug fix per PR.
- Ensure the project still builds cleanly with `dotnet build`.
- Reference any related issues with `Closes #<number>` in the PR body.
- Screenshots are highly appreciated for UI changes.

---

## Project Architecture

See **[docs/WIKI.md](docs/WIKI.md)** and **[docs/FILE-STRUCTURE.md](docs/FILE-STRUCTURE.md)** for a detailed description of the codebase layout and architecture.

---

*Thank you for contributing to FANZi! — Ionity Global (Pty) Ltd*
