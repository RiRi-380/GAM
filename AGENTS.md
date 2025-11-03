# Repository Guidelines

## Project Structure & Module Organization
- src/GmodAddonManager.Core: Core models, services, and utilities (non-UI logic).
- src/GmodAddonManager.UI: Avalonia UI app (Views .axaml, ViewModels, Converters, Services, Resources).
- scripts: Release helpers (PowerShell/Bash).
- installer: Inno Setup script for Windows installer.
- docs: Architecture and Steam/Workshop notes.
- publish, dist: Build outputs created by release scripts.

## Build, Test, and Development Commands
- Restore: `dotnet restore`
- Debug build: `dotnet build src/GmodAddonManager.UI/GmodAddonManager.UI.csproj -c Debug`
- Run UI: `dotnet run --project src/GmodAddonManager.UI/GmodAddonManager.UI.csproj`
- Clean build (script): `./clean-build.ps1`
- Quick debug (Windows): `./build-debug.ps1`
- Release (Windows): `./build-release.ps1 -Version vX.Y.Z` (outputs ZIP and optional installer; copies `steam_api64.dll` when available)

## Coding Style & Naming Conventions
- Language/TFM: C# 10, `net6.0`, `Nullable` enabled.
- Analyzers: .NET analyzers on (see `Directory.Build.props`). Fix warnings where practical.
- Indentation: 4 spaces; UTF-8 source files.
- Naming: `PascalCase` for types/properties/methods; `camelCase` for locals/params; interfaces prefixed `I` (e.g., `IIconResolver`); async methods end with `Async`.
- UI patterns: MVVM. ViewModels end with `ViewModel`; Views are `.axaml` + `.axaml.cs` with matching type names.

## Testing Guidelines
- Current state: No test project in repo. Prefer xUnit when adding tests.
- Structure: `tests/GmodAddonManager.Core.Tests` targeting service classes; optional `...UI.Tests` for view-models.
- Naming: File `{TypeName}Tests.cs`; method `MethodName_State_Expected`.
- Run: `dotnet test`. Aim for meaningful coverage on Core services; avoid UI snapshot tests unless justified.

## Commit & Pull Request Guidelines
- Commits: Follow Conventional Commits (e.g., `feat(core): add state store`, `fix(ui): prevent crash`). Small, focused changes; present tense, imperative mood.
- PRs: Include clear description, linked issues (e.g., `#123`), and screenshots/GIFs for UI changes. Note build/test steps and any migration notes.
- Validation: Ensure `dotnet build` succeeds, no analyzer regressions, and app still launches (`dotnet run` or built exe in `publish`).

## Security & Configuration Tips
- Keep `steam_api64.dll` and `steam_appid.txt` with outputs for Workshop features; do not hardcode secrets.
- Use `launch_gam_with_steam.bat` or follow docs to ensure Steam context when testing Workshop interactions.
