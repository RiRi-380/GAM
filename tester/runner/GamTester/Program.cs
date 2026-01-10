using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;

namespace GamTester;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options == null)
        {
            Options.PrintUsage();
            return 1;
        }

        Directory.CreateDirectory(options.ResultsDirectory);

        var dataset = await DatasetDefinition.LoadAsync(options.DatasetPath);
        var scenario = await ScenarioDefinition.LoadAsync(options.ScenarioPath);
        var condition = options.Condition ?? scenario.Condition ?? "LM";
        var mode = options.Mode ?? "local";

        SteamCmdConfig? steamConfig = null;
        if (mode.Equals("steamcmd", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(options.SteamUser))
            {
                Console.Error.WriteLine("steamcmd mode requires --steam-user.");
                return 1;
            }

            steamConfig = new SteamCmdConfig(
                Path.GetFullPath(options.SteamCmdPath ?? "steamcmd"),
                options.SteamUser!,
                options.SteamPassword,
                options.SteamGuard,
                options.SteamLibrary);
        }

        for (var i = 0; i < options.Repeat; i++)
        {
            var runId = Guid.NewGuid().ToString("N");
            var runRoot = options.WorkRoot ?? Path.Combine(Path.GetTempPath(), "gam-tester", runId);
            Directory.CreateDirectory(runRoot);

            RunResult result;
            try
            {
                TestEnvironment environment;
                if (steamConfig != null)
                {
                    environment = await SteamCmdEnvironmentBuilder.BuildAsync(runRoot, dataset, steamConfig, scenario.InitialEnabledGroups);
                }
                else
                {
                    environment = await TestEnvironmentBuilder.BuildAsync(runRoot, dataset, scenario.InitialEnabledGroups);
                }

                var expectedEnabled = scenario.ResolveExpectedEnabled(dataset);

                var stopwatch = Stopwatch.StartNew();
                if (string.Equals(condition, "LM", StringComparison.OrdinalIgnoreCase))
                {
                    result = await LmRunner.ExecuteAsync(environment, dataset, scenario, expectedEnabled);
                }
                else
                {
                    result = await BlRunner.ExecuteAsync(environment, dataset, scenario, expectedEnabled);
                }

                stopwatch.Stop();
                result.ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                result.Condition = condition.ToUpperInvariant();
                result.RunId = runId;
                result.Scenario = scenario.Name;
                result.Dataset = dataset.Name;
            }
            catch (Exception ex)
            {
                result = new RunResult
                {
                    Condition = condition.ToUpperInvariant(),
                    RunId = runId,
                    Scenario = scenario.Name,
                    Dataset = dataset.Name,
                    Success = false,
                    Error = ex.Message,
                    Steps = scenario.Actions.Count
                };
            }

            CsvWriter.Append(options.ResultsPath, result);
        }

        return 0;
    }
}

internal sealed class Options
{
    public string DatasetPath { get; private set; } = string.Empty;
    public string ScenarioPath { get; private set; } = string.Empty;
    public string ResultsPath { get; private set; } = string.Empty;
    public string ResultsDirectory => Path.GetDirectoryName(ResultsPath) ?? ".";
    public string? Condition { get; private set; }
    public int Repeat { get; private set; } = 1;
    public string? WorkRoot { get; private set; }
    public string? Mode { get; private set; }
    public string? SteamCmdPath { get; private set; }
    public string? SteamUser { get; private set; }
    public string? SteamPassword { get; private set; }
    public string? SteamGuard { get; private set; }
    public string? SteamLibrary { get; private set; }

    public static Options? Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return null;
        }

        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--dataset":
                    options.DatasetPath = args[++i];
                    break;
                case "--scenario":
                    options.ScenarioPath = args[++i];
                    break;
                case "--condition":
                    options.Condition = args[++i];
                    break;
                case "--repeat":
                    options.Repeat = int.TryParse(args[++i], out var repeat) ? repeat : 1;
                    break;
                case "--results":
                    options.ResultsPath = args[++i];
                    break;
                case "--workroot":
                    options.WorkRoot = args[++i];
                    break;
                case "--mode":
                    options.Mode = args[++i];
                    break;
                case "--steamcmd-path":
                    options.SteamCmdPath = args[++i];
                    break;
                case "--steam-user":
                    options.SteamUser = args[++i];
                    break;
                case "--steam-password":
                    options.SteamPassword = args[++i];
                    break;
                case "--steam-guard":
                    options.SteamGuard = args[++i];
                    break;
                case "--steam-library":
                    options.SteamLibrary = args[++i];
                    break;
                default:
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(options.DatasetPath) ||
            string.IsNullOrWhiteSpace(options.ScenarioPath))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(options.ResultsPath))
        {
            options.ResultsPath = Path.Combine("tester", "results", "runs.csv");
        }

        return options;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("Usage: dotnet run --project tester/runner/GamTester/GamTester.csproj -- --dataset <path> --scenario <path> [--condition LM|BL] [--repeat N] [--results <csv>] [--workroot <path>] [--mode local|steamcmd] [--steamcmd-path <path>] [--steam-user <user>] [--steam-password <pass>] [--steam-guard <code>] [--steam-library <path>]");
    }
}

internal sealed class DatasetDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "dataset";

    [JsonPropertyName("addons")]
    public List<AddonDefinition> Addons { get; set; } = new();

    public Dictionary<string, List<string>> BuildGroupIndex()
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var addon in Addons)
        {
            if (!index.ContainsKey(addon.Group))
            {
                index[addon.Group] = new List<string>();
            }
            index[addon.Group].Add(addon.Id);
        }
        return index;
    }

    public static async Task<DatasetDefinition> LoadAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var dataset = await JsonSerializer.DeserializeAsync<DatasetDefinition>(stream, JsonOptions.Default);
        return dataset ?? new DatasetDefinition();
    }
}

internal sealed class AddonDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("group")]
    public string Group { get; set; } = "A";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "gma"; // gma or folder

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; } = 1024 * 1024;
}

internal sealed class ScenarioDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "scenario";

    [JsonPropertyName("condition")]
    public string? Condition { get; set; }

    [JsonPropertyName("actions")]
    public List<ActionDefinition> Actions { get; set; } = new();

    [JsonPropertyName("expected_enabled")]
    public List<string>? ExpectedEnabled { get; set; }

    [JsonPropertyName("expected_enabled_groups")]
    public List<string>? ExpectedEnabledGroups { get; set; }

    [JsonPropertyName("initial_enabled_groups")]
    public List<string>? InitialEnabledGroups { get; set; }

    public static async Task<ScenarioDefinition> LoadAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var scenario = await JsonSerializer.DeserializeAsync<ScenarioDefinition>(stream, JsonOptions.Default);
        return scenario ?? new ScenarioDefinition();
    }

    public HashSet<string> ResolveExpectedEnabled(DatasetDefinition dataset)
    {
        if (ExpectedEnabled is { Count: > 0 })
        {
            return new HashSet<string>(ExpectedEnabled);
        }

        if (ExpectedEnabledGroups is { Count: > 0 })
        {
            var groups = dataset.BuildGroupIndex();
            var ids = ExpectedEnabledGroups
                .SelectMany(g =>
                {
                    return groups.TryGetValue(g, out var list)
                        ? (IEnumerable<string>)list
                        : Array.Empty<string>();
                });
            return new HashSet<string>(ids);
        }

        return new HashSet<string>();
    }
}

internal sealed class ActionDefinition
{
    [JsonPropertyName("op")]
    public string Op { get; set; } = string.Empty;

    [JsonPropertyName("asset")]
    public string? Asset { get; set; }

    [JsonPropertyName("ids")]
    public List<string>? Ids { get; set; }

    [JsonPropertyName("group")]
    public string? Group { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonPropertyName("sleep_ms")]
    public int? SleepMs { get; set; }
}

internal sealed class RunResult
{
    public string RunId { get; set; } = string.Empty;
    public string Scenario { get; set; } = string.Empty;
    public string Dataset { get; set; } = string.Empty;
    public string Condition { get; set; } = string.Empty;
    public double ElapsedMilliseconds { get; set; }
    public int Steps { get; set; }
    public int UndoCalls { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public long WriteBytes { get; set; }
    public int SteamSyncCount { get; set; }
}

internal sealed class TestEnvironment
{
    public TestEnvironment(string rootPath, string workshopPath, string sourcePath, DatasetDefinition dataset, Dictionary<string, List<string>> groupIndex, bool useSteamCmd = false, SteamCmdConfig? steamConfig = null, string? steamLogPath = null)
    {
        RootPath = rootPath;
        WorkshopPath = workshopPath;
        SourcePath = sourcePath;
        Dataset = dataset;
        GroupIndex = groupIndex;
        UseSteamCmd = useSteamCmd;
        SteamConfig = steamConfig;
        SteamLogPath = steamLogPath;
    }

    public string RootPath { get; }
    public string WorkshopPath { get; }
    public string SourcePath { get; }
    public DatasetDefinition Dataset { get; }
    public Dictionary<string, List<string>> GroupIndex { get; }
    public bool UseSteamCmd { get; }
    public SteamCmdConfig? SteamConfig { get; }
    public string? SteamLogPath { get; }
}

internal static class TestEnvironmentBuilder
{
    public static async Task<TestEnvironment> BuildAsync(string root, DatasetDefinition dataset, List<string>? initialEnabledGroups)
    {
        var groupIndex = dataset.BuildGroupIndex();
        var workshopPath = Path.Combine(root, "workshop", "content", "4000");
        var sourcePath = Path.Combine(root, "source");

        Directory.CreateDirectory(workshopPath);
        Directory.CreateDirectory(sourcePath);

        foreach (var addon in dataset.Addons)
        {
            var sourceAddonPath = GetAddonSourcePath(sourcePath, addon);
            await CreateAddonAsync(sourceAddonPath, addon);
        }

        // 初期有効化: 指定がなければ全グループを有効化
        var groupsToEnable = initialEnabledGroups is { Count: > 0 }
            ? initialEnabledGroups
            : groupIndex.Keys.ToList();

        foreach (var group in groupsToEnable)
        {
            if (!groupIndex.TryGetValue(group, out var ids)) continue;
            foreach (var id in ids)
            {
                var addon = dataset.Addons.First(a => a.Id == id);
                await EnableAddonFilesAsync(sourcePath, workshopPath, addon);
            }
        }

        return new TestEnvironment(root, workshopPath, sourcePath, dataset, groupIndex);
    }

    private static string GetAddonSourcePath(string sourceRoot, AddonDefinition addon)
    {
        if (IsGma(addon))
        {
            return Path.Combine(sourceRoot, addon.Id, $"{addon.Id}.gma");
        }

        return Path.Combine(sourceRoot, addon.Id);
    }

    private static bool IsGma(AddonDefinition addon) =>
        addon.Type.Equals("gma", StringComparison.OrdinalIgnoreCase);

    private static async Task CreateAddonAsync(string path, AddonDefinition addon)
    {
        if (IsGma(addon))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await WriteDummyFileAsync(path, addon.SizeBytes);
        }
        else
        {
            Directory.CreateDirectory(path);
            var filePath = Path.Combine(path, "file.txt");
            await WriteDummyFileAsync(filePath, addon.SizeBytes);
        }
    }

    public static async Task EnableAddonFilesAsync(string sourceRoot, string workshopRoot, AddonDefinition addon)
    {
        var sourcePath = GetAddonSourcePath(sourceRoot, addon);
        var targetDir = Path.Combine(workshopRoot, addon.Id);
        Directory.CreateDirectory(targetDir);

        if (IsGma(addon))
        {
            var targetPath = Path.Combine(targetDir, $"{addon.Id}.gma");
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath, overwrite: true);
        }
        else
        {
            CopyDirectory(Path.GetDirectoryName(sourcePath)!, targetDir);
        }
    }

    public static void DisableAddonFiles(string workshopRoot, AddonDefinition addon)
    {
        var targetDir = Path.Combine(workshopRoot, addon.Id);
        if (Directory.Exists(targetDir))
        {
            Directory.Delete(targetDir, true);
        }
    }

    private static async Task WriteDummyFileAsync(string path, long sizeBytes)
    {
        var buffer = new byte[8192];
        var random = new Random(0);
        await using var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var remaining = sizeBytes;
        while (remaining > 0)
        {
            random.NextBytes(buffer);
            var toWrite = (int)Math.Min(buffer.Length, remaining);
            await stream.WriteAsync(buffer.AsMemory(0, toWrite));
            remaining -= toWrite;
        }
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var dest = Path.Combine(targetDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }
}

internal static class SteamCmdEnvironmentBuilder
{
    public static async Task<TestEnvironment> BuildAsync(string root, DatasetDefinition dataset, SteamCmdConfig config, List<string>? initialEnabledGroups)
    {
        var groupIndex = dataset.BuildGroupIndex();
        var installDir = config.LibraryPath ?? Path.Combine(root, "steamcmd-library", "steamapps");
        var workshopPath = Path.Combine(installDir, "workshop", "content", "4000");
        var logsRoot = Directory.GetParent(installDir)?.FullName ?? installDir;
        var logPath = Path.Combine(logsRoot, "logs", "workshop_log.txt");

        Directory.CreateDirectory(workshopPath);

        // 事前に全IDをダウンロード（LMがハードリンク/ジャンクションで使う想定）
        await SteamCmdRunner.DownloadAsync(config with { LibraryPath = installDir }, dataset.Addons.Select(a => a.Id));

        // 初期有効化: 指定がある場合のみ対象外を削除
        if (initialEnabledGroups is { Count: > 0 })
        {
            var keepIds = new HashSet<string>(initialEnabledGroups.SelectMany(g =>
                groupIndex.TryGetValue(g, out var ids) ? ids : Enumerable.Empty<string>()));

            foreach (var dir in Directory.GetDirectories(workshopPath))
            {
                var id = Path.GetFileName(dir);
                if (!string.IsNullOrWhiteSpace(id) && !keepIds.Contains(id))
                {
                    Directory.Delete(dir, true);
                }
            }
        }

        // SourcePath は workshopPath を使い回す（BLのコピー経路はSteamCMD側で処理）
        return new TestEnvironment(root, workshopPath, workshopPath, dataset, groupIndex, useSteamCmd: true, steamConfig: config with { LibraryPath = installDir }, steamLogPath: logPath);
    }
}

internal static class LmRunner
{
    public static async Task<RunResult> ExecuteAsync(TestEnvironment env, DatasetDefinition dataset, ScenarioDefinition scenario, HashSet<string> expectedEnabled)
    {
        var errorHandler = new NullErrorHandler();
        var manager = new AddonManager(env.WorkshopPath, errorHandler);
        await manager.InitializeAsync();

        var steps = 0;
        var undoCalls = 0;
        var beforeSize = MetricsHelper.GetTotalSize(env.WorkshopPath);
        var beforeLog = MetricsHelper.ReadLog(env.SteamLogPath);

        foreach (var action in scenario.Actions)
        {
            steps++;
            var op = action.Op.ToLowerInvariant();
            switch (op)
            {
                case "create_asset":
                    if (action.Asset != null) manager.CreateAsset(action.Asset);
                    break;
                case "add_to_asset":
                    if (action.Asset != null && action.Ids != null)
                    {
                        foreach (var id in action.Ids)
                        {
                            manager.AddAddonToAsset(action.Asset, id, ParseAddonState(action.State));
                        }
                    }
                    break;
                case "add_group_to_asset":
                    if (action.Asset != null && action.Group != null && env.GroupIndex.TryGetValue(action.Group, out var ids))
                    {
                        manager.AddAddonsToAssetBatch(action.Asset, ids, ParseAddonState(action.State));
                    }
                    break;
                case "enable_asset":
                    if (action.Asset != null) await manager.EnableAssetAsync(action.Asset);
                    break;
                case "disable_asset":
                    if (action.Asset != null) await manager.DisableAssetAsync(action.Asset);
                    break;
                case "enable_addon":
                    if (action.Ids != null) foreach (var id in action.Ids) manager.EnableAddon(id);
                    break;
                case "disable_addon":
                    if (action.Ids != null) foreach (var id in action.Ids) manager.DisableAddon(id);
                    break;
                case "set_disable_mode":
                    manager.DisableMode = ParseDisableMode(action.Mode);
                    break;
                case "undo":
                    undoCalls++;
                    await manager.UndoLastActionAsync();
                    break;
                case "sleep":
                case "sleep_ms":
                    if (action.SleepMs is > 0) await Task.Delay(action.SleepMs.Value);
                    break;
                default:
                    break;
            }
        }

        var actualEnabled = new HashSet<string>(manager.GetEnabledAddons());
        var success = !expectedEnabled.Any() || expectedEnabled.SetEquals(actualEnabled);
        var afterSize = MetricsHelper.GetTotalSize(env.WorkshopPath);
        var afterLog = MetricsHelper.ReadLog(env.SteamLogPath);

        return new RunResult
        {
            Success = success,
            Steps = steps,
            UndoCalls = undoCalls,
            WriteBytes = MetricsHelper.EstimateWriteBytes(beforeSize, afterSize),
            SteamSyncCount = MetricsHelper.EstimateSteamSyncEvents(beforeLog, afterLog)
        };
    }

    private static AddonState ParseAddonState(string? value)
    {
        return value?.ToLowerInvariant() switch
        {
            "disabled" => AddonState.Disabled,
            "excluded" => AddonState.Excluded,
            _ => AddonState.Enabled
        };
    }

    private static DisableMode ParseDisableMode(string? value)
    {
        return value?.ToLowerInvariant() switch
        {
            "hard" => DisableMode.Hard,
            _ => DisableMode.Soft
        };
    }
}

internal static class BlRunner
{
    private sealed class AssetState
    {
        public string Name { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        public HashSet<string> Addons { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static async Task<RunResult> ExecuteAsync(TestEnvironment env, DatasetDefinition dataset, ScenarioDefinition scenario, HashSet<string> expectedEnabled)
    {
        var steps = 0;
        var undoCalls = 0;
        var assets = new Dictionary<string, AssetState>(StringComparer.OrdinalIgnoreCase);
        var beforeSize = MetricsHelper.GetTotalSize(env.WorkshopPath);
        var beforeLog = MetricsHelper.ReadLog(env.SteamLogPath);

        foreach (var action in scenario.Actions)
        {
            steps++;
            var op = action.Op.ToLowerInvariant();

            switch (op)
            {
                case "create_asset":
                    if (action.Asset != null && !assets.ContainsKey(action.Asset))
                    {
                        assets[action.Asset] = new AssetState { Name = action.Asset };
                    }
                    break;
                case "add_to_asset":
                    if (action.Asset != null && action.Ids != null && assets.TryGetValue(action.Asset, out var asset))
                    {
                        foreach (var id in action.Ids) asset.Addons.Add(id);
                    }
                    break;
                case "add_group_to_asset":
                    if (action.Asset != null && action.Group != null && assets.TryGetValue(action.Asset, out var target) &&
                        env.GroupIndex.TryGetValue(action.Group, out var ids))
                    {
                        foreach (var id in ids) target.Addons.Add(id);
                    }
                    break;
                case "enable_asset":
                    if (action.Asset != null && assets.TryGetValue(action.Asset, out var enableAsset))
                    {
                        enableAsset.Enabled = true;
                        var idsToEnable = enableAsset.Addons.ToList();
                        if (env.UseSteamCmd && env.SteamConfig != null)
                        {
                            await SteamCmdRunner.DownloadAsync(env.SteamConfig, idsToEnable);
                        }
                        else
                        {
                            foreach (var id in idsToEnable)
                            {
                                var addon = dataset.Addons.First(a => a.Id == id);
                                await TestEnvironmentBuilder.EnableAddonFilesAsync(env.SourcePath, env.WorkshopPath, addon);
                            }
                        }
                    }
                    break;
                case "disable_asset":
                    if (action.Asset != null && assets.TryGetValue(action.Asset, out var disableAsset))
                    {
                        disableAsset.Enabled = false;
                        foreach (var id in disableAsset.Addons)
                        {
                            var addon = dataset.Addons.First(a => a.Id == id);
                            TestEnvironmentBuilder.DisableAddonFiles(env.WorkshopPath, addon);
                        }
                    }
                    break;
                case "enable_addon":
                    if (action.Ids != null)
                    {
                        if (env.UseSteamCmd && env.SteamConfig != null)
                        {
                            await SteamCmdRunner.DownloadAsync(env.SteamConfig, action.Ids);
                        }
                        else
                        {
                            foreach (var id in action.Ids)
                            {
                                var addon = dataset.Addons.First(a => a.Id == id);
                                await TestEnvironmentBuilder.EnableAddonFilesAsync(env.SourcePath, env.WorkshopPath, addon);
                            }
                        }
                    }
                    break;
                case "disable_addon":
                    if (action.Ids != null)
                    {
                        foreach (var id in action.Ids)
                        {
                            var addon = dataset.Addons.First(a => a.Id == id);
                            TestEnvironmentBuilder.DisableAddonFiles(env.WorkshopPath, addon);
                        }
                    }
                    break;
                case "undo":
                    undoCalls++;
                    // BL側はUndoなし（将来拡張用）
                    break;
                case "sleep":
                case "sleep_ms":
                    if (action.SleepMs is > 0) await Task.Delay(action.SleepMs.Value);
                    break;
                default:
                    break;
            }
        }

        var actualEnabled = EnumerateEnabled(env.WorkshopPath);
        var success = !expectedEnabled.Any() || expectedEnabled.SetEquals(actualEnabled);
        var afterSize = MetricsHelper.GetTotalSize(env.WorkshopPath);
        var afterLog = MetricsHelper.ReadLog(env.SteamLogPath);

        return new RunResult
        {
            Success = success,
            Steps = steps,
            UndoCalls = undoCalls,
            WriteBytes = MetricsHelper.EstimateWriteBytes(beforeSize, afterSize),
            SteamSyncCount = MetricsHelper.EstimateSteamSyncEvents(beforeLog, afterLog)
        };
    }

    private static HashSet<string> EnumerateEnabled(string workshopPath)
    {
        if (!Directory.Exists(workshopPath))
        {
            return new HashSet<string>();
        }

        var ids = Directory.GetDirectories(workshopPath)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name) && !name.StartsWith("."));

        return new HashSet<string>(ids!);
    }
}

internal sealed record SteamCmdConfig(
    string ExecutablePath,
    string User,
    string? Password,
    string? GuardCode,
    string? LibraryPath);

internal sealed class LogSnapshot
{
    public string? Path { get; init; }
    public int LineCount { get; init; }
    public long Length { get; init; }
    public DateTime? LastWrite { get; init; }
}

internal static class MetricsHelper
{
    public static long GetTotalSize(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return 0;
            return Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
        }
        catch
        {
            return 0;
        }
    }

    public static LogSnapshot ReadLog(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new LogSnapshot { Path = path, LineCount = 0, Length = 0, LastWrite = null };
        }

        try
        {
            var lines = File.ReadAllLines(path);
            var info = new FileInfo(path);
            return new LogSnapshot
            {
                Path = path,
                LineCount = lines.Length,
                Length = info.Length,
                LastWrite = info.LastWriteTimeUtc
            };
        }
        catch
        {
            return new LogSnapshot { Path = path, LineCount = 0, Length = 0, LastWrite = null };
        }
    }

    public static long EstimateWriteBytes(long before, long after)
    {
        var delta = after - before;
        return delta > 0 ? delta : 0;
    }

    public static int EstimateSteamSyncEvents(LogSnapshot before, LogSnapshot after)
    {
        if (before.Path == null || after.Path == null) return 0;
        var deltaLines = after.LineCount - before.LineCount;
        return deltaLines > 0 ? deltaLines : 0;
    }
}

internal static class SteamCmdRunner
{
    public static async Task DownloadAsync(SteamCmdConfig config, IEnumerable<string> addonIds)
    {
        var ids = addonIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var args = BuildArgs(config, ids);

        var psi = new ProcessStartInfo
        {
            FileName = config.ExecutablePath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var proc = Process.Start(psi);
        if (proc == null)
        {
            throw new InvalidOperationException("Failed to start steamcmd process.");
        }

        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"steamcmd exited with {proc.ExitCode}. stderr={stderr}");
        }
    }

    private static string BuildArgs(SteamCmdConfig config, List<string> addonIds)
    {
        var parts = new List<string>
        {
            "+@ShutdownOnFailedCommand 1"
        };

        if (!string.IsNullOrWhiteSpace(config.LibraryPath))
        {
            parts.Add($"+force_install_dir \"{config.LibraryPath}\"");
        }

        var login = $"+login {config.User}";
        if (!string.IsNullOrWhiteSpace(config.Password))
        {
            login += $" {config.Password}";
            if (!string.IsNullOrWhiteSpace(config.GuardCode))
            {
                login += $" {config.GuardCode}";
            }
        }
        parts.Add(login);

        foreach (var id in addonIds)
        {
            parts.Add($"+workshop_download_item 4000 {id}");
        }

        parts.Add("+quit");
        return string.Join(" ", parts);
    }
}

internal sealed class CsvWriter
{
    private static readonly string[] Headers = {
        "run_id","scenario","dataset","condition","elapsed_ms","steps","undo_calls","write_bytes","steam_sync","success","error"
    };

    public static void Append(string path, RunResult result)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var needsHeader = !File.Exists(path);
        using var writer = new StreamWriter(path, append: true);
        if (needsHeader)
        {
            writer.WriteLine(string.Join(",", Headers));
        }

        var line = string.Join(",",
            result.RunId,
            result.Scenario,
            result.Dataset,
            result.Condition,
            result.ElapsedMilliseconds.ToString("F2"),
            result.Steps,
            result.UndoCalls,
            result.WriteBytes,
            result.SteamSyncCount,
            result.Success ? "1" : "0",
            Sanitize(result.Error));

        writer.WriteLine(line);
    }

    private static string Sanitize(string? input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        return "\"" + input.Replace("\"", "'") + "\"";
    }
}

internal sealed class NullErrorHandler : IErrorHandler
{
    public void HandleError(Exception ex, string context, ErrorSeverity severity = ErrorSeverity.Error) { }
    public void HandleInfo(string message, string context) { }
    public void HandleWarning(string message, string context) { }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}
