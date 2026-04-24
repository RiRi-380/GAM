using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.ExperimentRunner
{
    internal static class Program
    {
        private const string DefaultCondition = "LM-Soft";

        private static async Task<int> Main(string[] args)
        {
            if (!TryParseArgs(args, out var options, out var error))
            {
                Console.Error.WriteLine(error ?? "Invalid arguments.");
                PrintUsage();
                return 1;
            }

            var manifest = LoadManifest(options.ManifestPath);
            if (manifest == null)
            {
                Console.Error.WriteLine("Failed to load manifest.");
                return 1;
            }

            if (!Directory.Exists(manifest.WorkshopPath))
            {
                Console.Error.WriteLine($"Workshop path not found: {manifest.WorkshopPath}");
                return 1;
            }

            var canonicalLogPath = options.CanonicalLogPath ?? Path.Combine(Path.GetDirectoryName(options.ManifestPath) ?? ".", $"canonical_{manifest.RunId}.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(canonicalLogPath) ?? ".");

            var eventLogPath = options.EventLogPath ?? Path.Combine(Path.GetDirectoryName(options.ManifestPath) ?? ".", $"events_{manifest.RunId}.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(eventLogPath) ?? ".");

            var experimentId = options.ExperimentId ?? manifest.RunId;
            var condition = options.Condition ?? DefaultCondition;

            Environment.SetEnvironmentVariable("GAM_EXPERIMENT_ID", experimentId);
            Environment.SetEnvironmentVariable("GAM_CONDITION", condition);
            Environment.SetEnvironmentVariable("GAM_TASK_ID", string.Empty);
            Environment.SetEnvironmentVariable("GAM_EXPERIMENT_LOG_PATH", eventLogPath);
            Environment.SetEnvironmentVariable("GAM_EXPERIMENT_LOG", "1");

            var errorHandler = new ExperimentErrorHandler(Path.Combine(Path.GetDirectoryName(eventLogPath) ?? ".", "error_logs"));
            var managerOptions = new AddonManagerOptions
            {
                CustomWorkshopPath = manifest.WorkshopPath,
                CustomAppDataPath = Path.Combine(Path.GetDirectoryName(eventLogPath) ?? ".", "appdata"),
                DisableMode = DisableMode.Soft,
                ErrorHandler = errorHandler
            };

            var addonManager = new AddonManager(managerOptions);

            await addonManager.InitializeAsync();
            await addonManager.ScanWorkshopFolderAsync();

            PrepareConfiguration(addonManager, manifest);
            await addonManager.SaveConfigurationImmediatelyAsync();

            var assetIds = ResolveAssetIds(addonManager, new[] { "Base", "A", "B" });
            if (assetIds.Count != 3)
            {
                Console.Error.WriteLine("Failed to resolve Base/A/B assets.");
                return 1;
            }

            WriteCanonicalHeader(canonicalLogPath, manifest, experimentId, condition, eventLogPath);

            var repeats = options.RepeatsOverride ?? manifest.Repeats;
            for (var repeat = 1; repeat <= repeats; repeat++)
            {
                await addonManager.ApplyAssetExclusiveAsync(assetIds["Base"]);

                foreach (var task in manifest.Tasks)
                {
                    var fromAsset = task.From;
                    var toAsset = task.To;
                    var note = BuildNote(manifest, repeat, options.Note);

                    var beforeSnapshot = addonManager.CaptureState();
                    var beforeCanonical = ComputeCanonicalHash(manifest.AddonIds, beforeSnapshot.States);
                    var expectedCanonical = ComputeCanonicalHashFromEnabled(manifest.AddonIds, manifest.AssetSets[toAsset].Enabled);

                    addonManager.LogTaskStart(task.Id, out _, note: note, fromAssetId: assetIds[fromAsset], fromAssetLabel: fromAsset, toAssetId: assetIds[toAsset], toAssetLabel: toAsset);

                    await addonManager.ApplyAssetExclusiveAsync(assetIds[toAsset]);

                    var afterSnapshot = addonManager.CaptureState();
                    var afterCanonical = ComputeCanonicalHash(manifest.AddonIds, afterSnapshot.States);

                    addonManager.LogTaskEnd(task.Id, out _, note: note, fromAssetId: assetIds[fromAsset], fromAssetLabel: fromAsset, toAssetId: assetIds[toAsset], toAssetLabel: toAsset);

                    var ok = string.Equals(afterCanonical, expectedCanonical, StringComparison.OrdinalIgnoreCase);

                    WriteCanonicalEntry(canonicalLogPath, manifest, task, repeat, beforeCanonical, afterCanonical, expectedCanonical, ok, note);
                }
            }

            Console.WriteLine($"Run complete. Event log: {eventLogPath}");
            Console.WriteLine($"Canonical log: {canonicalLogPath}");
            return 0;
        }

        private static void PrepareConfiguration(AddonManager manager, RunManifest manifest)
        {
            var config = manager.GetConfiguration();
            config.Assets.RemoveAll(asset => !asset.IsSystem);

            foreach (var assetName in manifest.AssetSets.Keys)
            {
                manager.CreateAsset(assetName);
            }

            var assetsByName = config.Assets
                .Where(a => !a.IsSystem)
                .ToDictionary(a => a.Name, a => a, StringComparer.OrdinalIgnoreCase);

            foreach (var assetPair in manifest.AssetSets)
            {
                var assetName = assetPair.Key;
                if (!assetsByName.TryGetValue(assetName, out var asset))
                {
                    continue;
                }

                asset.DefaultAddonState = AddonState.Disabled;
                asset.Addons.Clear();
                asset.AddonStates.Clear();

                foreach (var addonId in assetPair.Value.Enabled)
                {
                    manager.AddAddonToAsset(asset.Id, addonId, AddonState.Enabled);
                }

                if (assetName.Equals("Base", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var addonId in manifest.AddonIds)
                    {
                        if (asset.AddonStates.ContainsKey(addonId))
                        {
                            continue;
                        }
                        manager.AddAddonToAsset(asset.Id, addonId, AddonState.Disabled);
                    }
                }
            }
        }

        private static Dictionary<string, string> ResolveAssetIds(AddonManager manager, IEnumerable<string> names)
        {
            var config = manager.GetConfiguration();
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                var asset = config.Assets.FirstOrDefault(a => !a.IsSystem && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
                if (asset != null)
                {
                    result[name] = asset.Id;
                }
            }
            return result;
        }

        private static string BuildNote(RunManifest manifest, int repeat, string? extra)
        {
            var note = $"env={manifest.EnvName};m={manifest.MSize};r={manifest.OverlapRatio};repeat={repeat};seed={manifest.Seed}";
            if (!string.IsNullOrWhiteSpace(extra))
            {
                note += $";{extra}";
            }
            return note;
        }

        private static string ComputeCanonicalHash(IEnumerable<string> addonIds, IReadOnlyDictionary<string, bool> states)
        {
            var normalized = BuildCanonicalNormalized(addonIds, id => states.TryGetValue(id, out var enabled) && enabled);
            return HashNormalized(normalized);
        }

        private static string ComputeCanonicalHashFromEnabled(IEnumerable<string> addonIds, IReadOnlyCollection<string> enabled)
        {
            var enabledSet = new HashSet<string>(enabled, StringComparer.Ordinal);
            var normalized = BuildCanonicalNormalized(addonIds, id => enabledSet.Contains(id));
            return HashNormalized(normalized);
        }

        private static string BuildCanonicalNormalized(IEnumerable<string> addonIds, Func<string, bool> isEnabled)
        {
            var ordered = addonIds
                .Select(id => new { Id = id, Numeric = ulong.TryParse(id, out var value) ? value : ulong.MaxValue })
                .OrderBy(x => x.Numeric)
                .ThenBy(x => x.Id, StringComparer.Ordinal)
                .ToList();

            var sb = new StringBuilder();
            for (var i = 0; i < ordered.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append('\n');
                }
                var entry = ordered[i];
                sb.Append(entry.Id).Append('=').Append(isEnabled(entry.Id) ? '1' : '0');
            }

            return sb.ToString();
        }

        private static string HashNormalized(string normalized)
        {
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(normalized);
            var hash = sha.ComputeHash(bytes);
            return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static void WriteCanonicalHeader(string path, RunManifest manifest, string experimentId, string condition, string eventLog)
        {
            var header = new CanonicalLogHeader
            {
                Event = "run_start",
                TimestampUtc = DateTime.UtcNow.ToString("O"),
                RunId = manifest.RunId,
                EnvName = manifest.EnvName,
                MSize = manifest.MSize,
                OverlapRatio = manifest.OverlapRatio,
                Seed = manifest.Seed,
                ExperimentId = experimentId,
                Condition = condition,
                EventLogPath = eventLog,
                AddonIds = manifest.AddonIds
            };

            AppendJsonLine(path, header);
        }

        private static void WriteCanonicalEntry(string path, RunManifest manifest, TaskSpec task, int repeat,
            string before, string after, string expected, bool ok, string note)
        {
            var entry = new CanonicalLogEntry
            {
                Event = "task_result",
                TimestampUtc = DateTime.UtcNow.ToString("O"),
                RunId = manifest.RunId,
                EnvName = manifest.EnvName,
                TaskId = task.Id,
                From = task.From,
                To = task.To,
                Repeat = repeat,
                CanonicalBefore = before,
                CanonicalAfter = after,
                CanonicalExpected = expected,
                CanonicalOk = ok,
                Note = note
            };

            AppendJsonLine(path, entry);
        }

        private static void AppendJsonLine<T>(string path, T entry)
        {
            var json = JsonSerializer.Serialize(entry);
            File.AppendAllText(path, json + Environment.NewLine);
        }

        private static RunManifest? LoadManifest(string path)
        {
            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<RunManifest>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Manifest load error: {ex.Message}");
                return null;
            }
        }

        private static bool TryParseArgs(string[] args, out RunnerOptions options, out string? error)
        {
            options = new RunnerOptions();
            error = null;

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "--manifest":
                        if (i + 1 >= args.Length)
                        {
                            error = "Missing value for --manifest";
                            return false;
                        }
                        options.ManifestPath = args[++i];
                        break;
                    case "--canonical-log":
                        if (i + 1 >= args.Length)
                        {
                            error = "Missing value for --canonical-log";
                            return false;
                        }
                        options.CanonicalLogPath = args[++i];
                        break;
                    case "--event-log":
                        if (i + 1 >= args.Length)
                        {
                            error = "Missing value for --event-log";
                            return false;
                        }
                        options.EventLogPath = args[++i];
                        break;
                    case "--experiment-id":
                        if (i + 1 >= args.Length)
                        {
                            error = "Missing value for --experiment-id";
                            return false;
                        }
                        options.ExperimentId = args[++i];
                        break;
                    case "--condition":
                        if (i + 1 >= args.Length)
                        {
                            error = "Missing value for --condition";
                            return false;
                        }
                        options.Condition = args[++i];
                        break;
                    case "--note":
                        if (i + 1 >= args.Length)
                        {
                            error = "Missing value for --note";
                            return false;
                        }
                        options.Note = args[++i];
                        break;
                    case "--repeat":
                        if (i + 1 >= args.Length || !int.TryParse(args[++i], out var repeat))
                        {
                            error = "Invalid value for --repeat";
                            return false;
                        }
                        options.RepeatsOverride = repeat;
                        break;
                    case "-h":
                    case "--help":
                        error = "";
                        return false;
                    default:
                        error = $"Unknown argument: {arg}";
                        return false;
                }
            }

            if (string.IsNullOrWhiteSpace(options.ManifestPath))
            {
                error = "--manifest is required";
                return false;
            }

            return true;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage: dotnet run --project tools/GmodAddonManager.ExperimentRunner -- --manifest <path> [--canonical-log <path>] [--event-log <path>] [--experiment-id <id>] [--condition <label>] [--note <text>] [--repeat <n>]");
        }

        private sealed class RunnerOptions
        {
            public string ManifestPath { get; set; } = string.Empty;
            public string? CanonicalLogPath { get; set; }
            public string? EventLogPath { get; set; }
            public string? ExperimentId { get; set; }
            public string? Condition { get; set; }
            public string? Note { get; set; }
            public int? RepeatsOverride { get; set; }
        }

        private sealed class RunManifest
        {
            public string RunId { get; set; } = string.Empty;
            public string EnvName { get; set; } = string.Empty;
            public string WorkshopPath { get; set; } = string.Empty;
            public List<string> AddonIds { get; set; } = new();
            public Dictionary<string, AssetSet> AssetSets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public List<TaskSpec> Tasks { get; set; } = new();
            public int Repeats { get; set; } = 10;
            public int MSize { get; set; }
            public double OverlapRatio { get; set; }
            public int Seed { get; set; }
        }

        private sealed class AssetSet
        {
            public List<string> Enabled { get; set; } = new();
        }

        private sealed class TaskSpec
        {
            public string Id { get; set; } = string.Empty;
            public string From { get; set; } = string.Empty;
            public string To { get; set; } = string.Empty;
        }

        private sealed class CanonicalLogHeader
        {
            public string Event { get; set; } = "run_start";
            public string TimestampUtc { get; set; } = string.Empty;
            public string RunId { get; set; } = string.Empty;
            public string EnvName { get; set; } = string.Empty;
            public int MSize { get; set; }
            public double OverlapRatio { get; set; }
            public int Seed { get; set; }
            public string ExperimentId { get; set; } = string.Empty;
            public string Condition { get; set; } = string.Empty;
            public string EventLogPath { get; set; } = string.Empty;
            public List<string> AddonIds { get; set; } = new();
        }

        private sealed class CanonicalLogEntry
        {
            public string Event { get; set; } = "task_result";
            public string TimestampUtc { get; set; } = string.Empty;
            public string RunId { get; set; } = string.Empty;
            public string EnvName { get; set; } = string.Empty;
            public string TaskId { get; set; } = string.Empty;
            public string From { get; set; } = string.Empty;
            public string To { get; set; } = string.Empty;
            public int Repeat { get; set; }
            public string CanonicalBefore { get; set; } = string.Empty;
            public string CanonicalAfter { get; set; } = string.Empty;
            public string CanonicalExpected { get; set; } = string.Empty;
            public bool CanonicalOk { get; set; }
            public string Note { get; set; } = string.Empty;
        }

        private sealed class ExperimentErrorHandler : IErrorHandler
        {
            private readonly string _logDirectory;

            public ExperimentErrorHandler(string logDirectory)
            {
                _logDirectory = logDirectory;
                Directory.CreateDirectory(_logDirectory);
            }

            public void HandleError(Exception ex, string context, ErrorSeverity severity = ErrorSeverity.Error)
            {
                var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{severity}] {context}{Environment.NewLine}{ex}{Environment.NewLine}";
                Append("error", entry);
            }

            public void HandleInfo(string message, string context)
            {
                var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Info] {context}: {message}{Environment.NewLine}";
                Append("info", entry);
            }

            public void HandleWarning(string message, string context)
            {
                var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Warning] {context}: {message}{Environment.NewLine}";
                Append("warning", entry);
            }

            private void Append(string prefix, string entry)
            {
                try
                {
                    var path = Path.Combine(_logDirectory, $"{prefix}_{DateTime.Now:yyyyMMdd}.log");
                    File.AppendAllText(path, entry);
                }
                catch
                {
                    // Ignore logging failures in experiment runner.
                }
            }
        }
    }
}
