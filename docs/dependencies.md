# Dependencies

## Core (netstandard2.1)
- Microsoft.Data.Sqlite (8.0.29): SQLite provider used for the Workshop metadata cache (workshop.db).
- SQLitePCLRaw.bundle_e_sqlite3 (2.1.12): Native SQLite bundle pinned above the versions affected by GHSA-2m69-gcr7-jv3q.
- Newtonsoft.Json (13.0.4): JSON serialization/deserialization for metadata, settings, and cache payloads.
- Polly (8.6.5): Transient-fault handling (retry/backoff) for network and IO operations.
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

## Dependency audit snapshot
- Snapshot date: 2026-07-31
- Command:
  - `dotnet list GmodAddonManager.sln package --include-transitive --vulnerable`
- Status:
  - No known vulnerable packages.
  - `Microsoft.Data.Sqlite` stays on the supported 8.0 patch line for `netstandard2.1` compatibility; native SQLite is pinned explicitly so NuGet cannot resolve the older vulnerable transitive version.
