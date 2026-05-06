# Dependencies

## Core (netstandard2.1)
- Microsoft.Data.Sqlite (8.0.0): SQLite provider used for the Workshop metadata cache (workshop.db).
- Newtonsoft.Json (13.0.3): JSON serialization/deserialization for metadata, settings, and cache payloads.
- Polly (8.2.0): Transient-fault handling (retry/backoff) for network and IO operations.
- SkiaSharp (2.88.8): Image decode/resize/crop for Workshop thumbnails and previews.
- Microsoft.Win32.Registry (5.0.0): Registry access for Steam path detection on Windows.
- System.Management (5.0.0): WMI queries for system/process/drive discovery.

## UI (net10.0)
- Avalonia (11.3.14): UI framework.
- Avalonia.Desktop (11.3.14): Desktop platform support.
- Avalonia.Themes.Fluent (11.3.14): Fluent theme resources.
- Avalonia.ReactiveUI (11.3.9): MVVM/reactive bindings.
- Avalonia.Controls.DataGrid (11.3.13): DataGrid control.
- Avalonia.Controls.ItemsRepeater (11.1.5): Virtualized items repeater.
- Avalonia.Xaml.Behaviors (11.3.0.6): XAML behaviors for interaction logic.
- DynamicData (8.4.1): Dynamic collection transforms for ViewModels.
- Newtonsoft.Json (13.0.4): JSON handling in UI settings.
- System.Reactive (6.1.0): Reactive streams and scheduling.

## Tests (net10.0)
- Microsoft.NET.Test.Sdk (18.5.1): Test host and discovery runner for `dotnet test`.
- xUnit (2.9.3): Unit test framework for deterministic core regression tests.
- xunit.runner.visualstudio (3.1.5): VSTest adapter for xUnit execution.
- coverlet.collector (10.0.0): Code coverage data collector for CI/reporting.

## Dependency update policy (release branch)
- Vulnerability fixes: apply immediately (must pass restore/build/test/release smoke before merge).
- Patch/minor updates: apply in small batches, prioritizing `Newtonsoft.Json`, `System.Reactive`, and Avalonia ecosystem packages.
- Major updates and framework migration: execute in dedicated hardening branch with UI smoke/regression passes.

## Outdated snapshot
- Snapshot date: 2026-05-06
- Command:
  - `dotnet list src/GmodAddonManager.UI/GmodAddonManager.UI.csproj package --outdated --include-transitive`
- Status:
  - No known vulnerable packages.
  - Multiple outdated packages remain (mainly Avalonia stack and transitive runtime dependencies).
