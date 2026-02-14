# Dependencies

## Core (netstandard2.1)
- Microsoft.Data.Sqlite (8.0.0): SQLite provider used for the Workshop metadata cache (workshop.db).
- Newtonsoft.Json (13.0.3): JSON serialization/deserialization for metadata, settings, and cache payloads.
- Polly (8.2.0): Transient-fault handling (retry/backoff) for network and IO operations.
- SkiaSharp (2.88.8): Image decode/resize/crop for Workshop thumbnails and previews.
- Microsoft.Win32.Registry (5.0.0): Registry access for Steam path detection on Windows.
- System.Management (5.0.0): WMI queries for system/process/drive discovery.

## UI (net6.0)
- Avalonia (11.0.7): UI framework.
- Avalonia.Desktop (11.0.7): Desktop platform support.
- Avalonia.Themes.Fluent (11.0.7): Fluent theme resources.
- Avalonia.ReactiveUI (11.0.7): MVVM/reactive bindings.
- Avalonia.Controls.DataGrid (11.0.7): DataGrid control.
- Avalonia.Controls.ItemsRepeater (11.0.7): Virtualized items repeater.
- Avalonia.Xaml.Behaviors (11.0.7): XAML behaviors for interaction logic.
- DynamicData (8.3.27): Dynamic collection transforms for ViewModels.
- Newtonsoft.Json (13.0.3): JSON handling in UI settings.
- System.Reactive (6.0.0): Reactive streams and scheduling.

## Tests (net6.0)
- Microsoft.NET.Test.Sdk (17.12.0): Test host and discovery runner for `dotnet test`.
- xUnit (2.9.2): Unit test framework for deterministic core regression tests.
- xunit.runner.visualstudio (2.8.2): VSTest adapter for xUnit execution.
- coverlet.collector (6.0.2): Code coverage data collector for CI/reporting.

## Dependency update policy (release branch)
- Vulnerability fixes: apply immediately (must pass restore/build/test/release smoke before merge).
- Patch/minor updates: apply in small batches, prioritizing `Newtonsoft.Json`, `System.Reactive`, and Avalonia ecosystem packages.
- Major updates and framework migration (`net6.0` -> `net8.0+`): execute in dedicated hardening branch with UI smoke/regression passes.

## Outdated snapshot
- Snapshot date: 2026-02-13
- Command:
  - `dotnet list src/GmodAddonManager.UI/GmodAddonManager.UI.csproj package --outdated --include-transitive`
- Status:
  - No known vulnerable packages.
  - Multiple outdated packages remain (mainly Avalonia stack and transitive runtime dependencies).
