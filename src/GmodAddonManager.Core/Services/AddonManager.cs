using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GmodAddonManager.Core.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace GmodAddonManager.Core.Services
{
    public enum DisableMode
    {
        Soft,
        Hard
    }

    public class AddonManager : IDisposable
    {
        private readonly string workshopPath;
        private readonly string managerPath;
        private readonly string configPath;
        private readonly string configurationPathMutexName;
        private readonly string pendingPath;
        private readonly string addonsPath;
        private readonly string? gmodCachePath;
        private readonly IReadOnlyList<string>? customWorkshopCacheFilePaths;
        private readonly int _maxParallelWorkshopScans;
        private string? gmodCacheManagerPath;
        private string? gmodCacheAddonsPath;
        private string? gmodRootPath;
        private string? localAddonsPath;
        private string? localGmaAddonsPath;
        private string? localRootGmaPath;
        private string? localManagedRootPath;
        private GmodAddonStateStore? gmodAddonStateStore;
        private readonly IAddonModeStrategy modeStrategy;
        
        private readonly JunctionService junctionService;
        private readonly SteamPathDetector steamPathDetector;
        private readonly WorkshopIconResolver workshopIconResolver;
        private readonly PathSnapshot? pathSnapshot;
        private readonly string? customGmodInstallPath;
        private readonly string? customWorkshopPath;
        private readonly SteamWorkshopService steamWorkshopService;
        private readonly UndoManager undoManager;
        private readonly AssetStateResolver assetStateResolver;
        private readonly AssetVersionService assetVersionService;
        private readonly ConfigurationMigrationService configurationMigrationService;
        private readonly GmodDisabledAddonReconciliationService gmodDisabledAddonReconciliationService;
        private readonly SubscriptionObservationService subscriptionObservationService;
        private readonly IErrorHandler errorHandler;
        private readonly ExperimentEventLogger eventLogger;
        private readonly AsyncLocal<LinkOperationMetrics?> linkMetricsContext = new AsyncLocal<LinkOperationMetrics?>();
        
        private readonly System.Threading.Timer _saveDebounceTimer;
        private readonly object _saveLock = new object();
        private bool _saveRequested = false;
        private int _saveDebounceMilliseconds = 1000; // デフォルト1秒
        private static readonly TimeSpan DisposeSaveFlushTimeout = TimeSpan.FromSeconds(5);
        private int _softModeNoFileOpsNoticeLogged = 0;
        private int _localManagementDisabledNoticeLogged = 0;
        private int _sessionLogged = 0;
        private bool _initializationCompleted;
        private readonly object _scanCacheLock = new object();
        private List<WorkshopAddon>? _scanCache;
        private DateTime _scanCacheTimestampUtc = DateTime.MinValue;
        private TimeSpan _scanCacheTtl = TimeSpan.Zero;
        private readonly AsyncLocal<int> bulkStateUpdateDepth = new AsyncLocal<int>();
        private readonly int _maxParallelAddonStateUpdates;
        private readonly SemaphoreSlim _configurationSaveGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _runtimeReconcileGate = new SemaphoreSlim(1, 1);
        private static readonly TimeSpan ConfigurationPathMutexTimeout =
            TimeSpan.FromSeconds(10);

        public DisableMode DisableMode { get; private set; }
        public bool StrictLinkMode
        {
            get => strictLinkMode;
            set
            {
                strictLinkMode = value;
                eventLogger.StrictLinkMode = value;
            }
        }
        public bool EnableLocalAddonManagement
        {
            get => enableLocalAddonManagement;
            set
            {
                if (enableLocalAddonManagement == value)
                {
                    return;
                }

                enableLocalAddonManagement = value;
                InvalidateWorkshopScanCache();
            }
        }
        public bool IsExperimentContextActive => eventLogger.IsExperimentContextActive;
        public Func<bool?>? GmodRunningProvider { get; set; }
        public Func<int?>? PendingChangeCountProvider { get; set; }
        public Action? QueueRuntimeApplyProvider
        {
            get => queueRuntimeApplyProvider;
            set
            {
                queueRuntimeApplyProvider = value;
                var queueRecoveredWrite = runtimeReapplyRequiredFromRecovery;
                var queueNewSubscriptions = runtimeApplyRequiredForNewSubscriptions;
                if (value != null && (queueRecoveredWrite || queueNewSubscriptions))
                {
                    try
                    {
                        value.Invoke();
                        if (queueRecoveredWrite)
                        {
                            var pendingIntent = configuration.PendingGamRuntimeWrite;
                            if (pendingIntent != null && !pendingIntent.ConflictDetected)
                            {
                                configuration.PendingGamRuntimeWrite = null;
                                try
                                {
                                    SaveConfigurationImmediatelySynchronously();
                                }
                                catch
                                {
                                    configuration.PendingGamRuntimeWrite = pendingIntent;
                                    throw;
                                }
                            }
                        }
                        runtimeReapplyRequiredFromRecovery = false;
                        runtimeApplyRequiredForNewSubscriptions = false;
                    }
                    catch (Exception ex)
                    {
                        runtimeReapplyRequiredFromRecovery = queueRecoveredWrite;
                        runtimeApplyRequiredForNewSubscriptions = queueNewSubscriptions;
                        errorHandler.HandleWarning(
                            $"Failed to queue deferred runtime apply: {ex.Message}",
                            "QueueRuntimeApplyProvider");
                    }
                }
            }
        }
        internal Func<Guid>? QueueRuntimeApplyTrackedProvider { get; set; }
        internal Func<Guid, bool>? ClearRuntimeApplyIfGenerationProvider { get; set; }
        public int PendingDownloadCount { get; private set; }
        public TimeSpan StateMatchTimeout { get; set; } = TimeSpan.FromSeconds(5);
        public int StateMatchPollIntervalMs { get; set; } = 200;
        public TimeSpan ScanCacheTtl
        {
            get => _scanCacheTtl;
            set => _scanCacheTtl = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        }
        
        private Configuration configuration = null!;
        private OperationLogManager operationLogManager = null!;

        private bool strictLinkMode;
        private bool enableLocalAddonManagement;
        private Action? queueRuntimeApplyProvider;
        private bool runtimeReapplyRequiredFromRecovery;
        private bool runtimeApplyRequiredForNewSubscriptions;
        private bool IsBulkStateUpdate => bulkStateUpdateDepth.Value > 0;

        private const int ERROR_NOT_SAME_DEVICE = 17;
        private const string SubscribeSystemAssetId = SystemAssetDefinitions.SubscribeId;
        private const string JunctionSystemAssetId = SystemAssetDefinitions.JunctionId;
        internal const string RuntimeAttributionConflictFailureCode =
            "runtime_attribution_conflict";
        private const string AssetImageDirectoryName = "asset-images";
        private const int AssetImageOutputSize = 512;
        private const float AssetImageCornerRadiusRatio = 0.15625f;

        public AddonManager() : this(new AddonManagerOptions())
        {
        }

        public AddonManager(string? customWorkshopPath) : this(new AddonManagerOptions
        {
            CustomWorkshopPath = customWorkshopPath
        })
        {
        }

        public AddonManager(string? customWorkshopPath, IErrorHandler? customErrorHandler) : this(new AddonManagerOptions
        {
            CustomWorkshopPath = customWorkshopPath,
            ErrorHandler = customErrorHandler
        })
        {
        }

        public AddonManager(string? customWorkshopPath, IErrorHandler? customErrorHandler, DisableMode disableMode) : this(new AddonManagerOptions
        {
            CustomWorkshopPath = customWorkshopPath,
            ErrorHandler = customErrorHandler,
            DisableMode = disableMode
        })
        {
        }

        public AddonManager(AddonManagerOptions? options)
        {
            options ??= new AddonManagerOptions();
            _scanCacheTtl = options.ScanCacheTtl;
            _maxParallelAddonStateUpdates = options.MaxParallelAddonStateUpdates.HasValue
                ? Math.Max(1, options.MaxParallelAddonStateUpdates.Value)
                : Math.Clamp(Environment.ProcessorCount, 2, 6);
            _maxParallelWorkshopScans = options.MaxParallelWorkshopScans.HasValue
                ? Math.Max(1, options.MaxParallelWorkshopScans.Value)
                : Math.Clamp(Environment.ProcessorCount, 2, 4);
            customGmodInstallPath = string.IsNullOrWhiteSpace(options.CustomGmodInstallPath)
                ? null
                : options.CustomGmodInstallPath;
            customWorkshopPath = string.IsNullOrWhiteSpace(options.CustomWorkshopPath)
                ? null
                : options.CustomWorkshopPath;
            steamPathDetector = new SteamPathDetector();
            junctionService = new JunctionService();
            
            // Initialize WorkshopIconResolver
            var appDataPath = !string.IsNullOrWhiteSpace(options.CustomAppDataPath)
                ? options.CustomAppDataPath
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "GmodAddonManager"
                );
            workshopIconResolver = new WorkshopIconResolver(steamPathDetector, null, appDataPath);
            
            steamWorkshopService = new SteamWorkshopService(workshopIconResolver);
            // Update the iconResolver with the workshop service reference
            workshopIconResolver.SetWorkshopService(steamWorkshopService);
            
            undoManager = new UndoManager();
            assetStateResolver = new AssetStateResolver();
            assetVersionService = new AssetVersionService();
            configurationMigrationService = new ConfigurationMigrationService();
            gmodDisabledAddonReconciliationService =
                new GmodDisabledAddonReconciliationService();
            subscriptionObservationService = new SubscriptionObservationService();
            errorHandler = options.ErrorHandler ?? new DefaultErrorHandler(
                Path.Combine(appDataPath, "logs"));
            eventLogger = ExperimentEventLogger.CreateDefault(appDataPath);
            StrictLinkMode = GetStrictLinkModeFromEnvironment();
            EnableLocalAddonManagement = options.EnableLocalAddonsExperimental;
            customWorkshopCacheFilePaths = options.CustomWorkshopCacheFilePaths;
            DisableMode = options.DisableMode;
            modeStrategy = DisableMode == DisableMode.Hard
                ? new HardAddonModeStrategy()
                : new SoftAddonModeStrategy();

            if (!string.IsNullOrWhiteSpace(customGmodInstallPath) ||
                !string.IsNullOrWhiteSpace(customWorkshopPath))
            {
                if (PathOverrideResolver.TryCreateSnapshot(
                        customGmodInstallPath,
                        customWorkshopPath,
                        out var overrideSnapshot,
                        out var overrideError))
                {
                    pathSnapshot = overrideSnapshot;
                    LogPathSnapshot(pathSnapshot, "Constructor.CustomPathOverride");
                }
                else
                {
                    errorHandler.HandleWarning($"Failed to use custom path override: {overrideError}", "Constructor");
                }
            }

            if (pathSnapshot == null)
            {
                try
                {
                    pathSnapshot = steamPathDetector.DetectPathSnapshot();
                    LogPathSnapshot(pathSnapshot, "Constructor");
                }
                catch (Exception ex)
                {
                    errorHandler.HandleWarning($"Failed to resolve Steam/GMod path snapshot: {ex.Message}", "Constructor");
                }
            }

            // A valid Workshop override remains useful even when the paired GMod
            // override cannot form a complete snapshot. Never adopt a raw,
            // unreadable Workshop path merely because it was configured.
            var hasCustomWorkshopPath =
                !string.IsNullOrWhiteSpace(customWorkshopPath) &&
                PathOverrideResolver.IsDirectoryUsable(customWorkshopPath);
            var detectedWorkshopPath = pathSnapshot?.ActiveWorkshopRoot?.RootPath;
            workshopPath = hasCustomWorkshopPath
                ? customWorkshopPath!
                : !string.IsNullOrWhiteSpace(detectedWorkshopPath) &&
                  PathOverrideResolver.IsDirectoryUsable(detectedWorkshopPath)
                    ? detectedWorkshopPath
                    : Path.Combine(appDataPath, "unavailable-workshop");

            if (DisableMode == DisableMode.Soft)
            {
                managerPath = appDataPath;
                configPath = Path.Combine(managerPath, "config.json");
                pendingPath = Path.Combine(managerPath, "pending.json");
                addonsPath = Path.Combine(managerPath, "addons");
            }
            else
            {
                managerPath = Path.Combine(workshopPath, ".addon-manager");
                configPath = Path.Combine(managerPath, "config.json");
                pendingPath = Path.Combine(managerPath, "pending.json");
                addonsPath = Path.Combine(managerPath, "addons");
            }

            configurationPathMutexName = BuildConfigurationPathMutexName(configPath);

            localManagedRootPath = Path.Combine(managerPath, "local-addons");
            
            // Detect Gmod cache path
            try
            {
                gmodCachePath = options.DisableCacheScan
                    ? null
                    : !string.IsNullOrWhiteSpace(options.CustomGmodCachePath)
                        ? options.CustomGmodCachePath
                        : !hasCustomWorkshopPath
                            ? pathSnapshot?.GmodCacheWorkshopPath ?? steamPathDetector.DetectGmodCachePath()
                            : steamPathDetector.DetectGmodCachePath();
                // エラーが発生してもnullとして続行
            }
            catch (Exception)
            {
                // Error detecting cache path
                // エラーが発生してもnullとして続行
                gmodCachePath = null;
            }
            
            // Set up cache manager paths
            if (DisableMode == DisableMode.Hard && !string.IsNullOrEmpty(gmodCachePath))
            {
                gmodCacheManagerPath = Path.Combine(gmodCachePath, ".addon-manager");
                gmodCacheAddonsPath = Path.Combine(gmodCacheManagerPath, "addons");
                errorHandler.HandleInfo($"Set gmodCacheManagerPath: {gmodCacheManagerPath}", "Constructor");
                errorHandler.HandleInfo($"Set gmodCacheAddonsPath: {gmodCacheAddonsPath}", "Constructor");
            }
            else
            {
                gmodCacheManagerPath = null;
                gmodCacheAddonsPath = null;
                if (string.IsNullOrEmpty(gmodCachePath))
                {
                    errorHandler.HandleWarning("gmodCachePath is null or empty, cache addon management will be disabled", "Constructor");
                }
            }

            // Detect Gmod root path for settings management (addonnomount.txt)
            try
            {
                var candidate = !string.IsNullOrWhiteSpace(customGmodInstallPath)
                    ? customGmodInstallPath
                    : hasCustomWorkshopPath
                        ? Path.GetFullPath(Path.Combine(workshopPath, @"..\..\..\common\GarrysMod"))
                        : pathSnapshot?.GmodInstall?.InstallPath;
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    candidate = Path.GetFullPath(Path.Combine(workshopPath, @"..\..\..\common\GarrysMod"));
                    errorHandler.HandleWarning(
                        "Garry's Mod appmanifest path was not resolved; falling back to workshop-relative path inference.",
                        "Constructor");
                }

                if (Directory.Exists(candidate))
                {
                    gmodRootPath = candidate;
                    gmodAddonStateStore = new GmodAddonStateStore(gmodRootPath);
                    errorHandler.HandleInfo($"Set gmodRootPath: {gmodRootPath}", "Constructor");
                    localAddonsPath = Path.Combine(gmodRootPath, "garrysmod", "addons");
                    localGmaAddonsPath = Path.Combine(localAddonsPath, "gma");
                    localRootGmaPath = Path.Combine(gmodRootPath, "garrysmod");
                }
                else
                {
                    errorHandler.HandleWarning("Garry's Mod root path not found; will not edit addonnomount.txt state store", "Constructor");
                }
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning($"Failed to resolve GMod root path: {ex.Message}", "Constructor");
            }
            
            // デバウンスタイマーの初期化
            _saveDebounceTimer = new System.Threading.Timer(
                OnSaveDebounceTimer,
                null,
                Timeout.Infinite,
                Timeout.Infinite
            );
        }

        private List<string> GetVisibleWorkshopDirectoriesOrEmpty(string operationName)
        {
            return GetWorkshopDirectoriesOrEmpty("*", operationName)
                .Where(path =>
                {
                    var name = Path.GetFileName(path);
                    return !string.IsNullOrEmpty(name) && !name.StartsWith(".", StringComparison.Ordinal);
                })
                .ToList();
        }

        private void LogPathSnapshot(PathSnapshot snapshot, string operationName)
        {
            errorHandler.HandleInfo(
                $"Path snapshot: steamRoot={snapshot.SteamRootPath ?? "<none>"}, " +
                $"gmod={snapshot.GmodInstall?.InstallPath ?? "<none>"} ({snapshot.GmodInstall?.Confidence.ToString() ?? "None"}), " +
                $"workshop={snapshot.ActiveWorkshopRoot?.RootPath ?? "<none>"} ({snapshot.ActiveWorkshopRoot?.Confidence.ToString() ?? "None"}), " +
                $"cache={snapshot.GmodCacheWorkshopPath ?? "<none>"}",
                operationName);

            foreach (var issue in snapshot.HealthIssues)
            {
                errorHandler.HandleWarning($"Path snapshot issue: {issue}", operationName);
            }
        }

        private PathSnapshot DetectCurrentPathSnapshot()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(customGmodInstallPath) ||
                    !string.IsNullOrWhiteSpace(customWorkshopPath))
                {
                    if (PathOverrideResolver.TryCreateSnapshot(
                            customGmodInstallPath,
                            customWorkshopPath,
                            out var overrideSnapshot,
                            out var overrideError))
                    {
                        LogPathSnapshot(overrideSnapshot, "PathHealth.CustomPathOverride");
                        return overrideSnapshot;
                    }

                    errorHandler.HandleWarning($"Failed to refresh custom path snapshot: {overrideError}", "PathHealth");
                }

                var snapshot = steamPathDetector.DetectPathSnapshot();
                LogPathSnapshot(snapshot, "PathHealth");
                return snapshot;
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning($"Failed to refresh path snapshot: {ex.Message}", "PathHealth");
                return pathSnapshot ?? new PathSnapshot
                {
                    HealthIssues = new[] { $"Failed to refresh path snapshot: {ex.Message}" }
                };
            }
        }

        private void RecordCurrentPathState()
        {
            configuration.PathState ??= new PathState();
            // The constructor already resolved the startup paths. Re-running discovery here
            // used to traverse every Workshop directory for a second time before any window
            // could be shown. Manual path-health operations still request a fresh snapshot.
            var snapshot = pathSnapshot ?? DetectCurrentPathSnapshot();
            PathHealthService.UpdatePathState(configuration, snapshot, managerPath, addonsPath);
        }

        public PathHealthReport GetPathHealthReport()
        {
            configuration.PathState ??= new PathState();
            var snapshot = DetectCurrentPathSnapshot();
            return PathHealthService.BuildReport(configuration, snapshot, managerPath, addonsPath);
        }

        public async Task<PathHealthOperationResult> RepairStalePathMetadataAsync()
        {
            var report = GetPathHealthReport();
            var result = PathHealthService.RepairMetadata(configuration, report.MetadataRepairCandidates);
            if (result.ChangedCount > 0)
            {
                await SaveConfigurationImmediatelyAsync();
                InvalidateWorkshopScanCache();
            }

            return result;
        }

        public async Task<PathHealthOperationResult> MigrateAddonNoMountEntriesAsync()
        {
            var report = GetPathHealthReport();
            var result = PathHealthService.MigrateAddonNoMountEntries(report.AddonNoMountMigrationPlan);
            if (result.ChangedCount > 0)
            {
                await SaveConfigurationImmediatelyAsync();
            }

            return result;
        }

        public async Task<PathHealthOperationResult> MigrateManagedDataAsync()
        {
            var report = GetPathHealthReport();
            var result = PathHealthService.MigrateManagedData(report.ManagedDataMigrationCandidates);
            var metadataUpdates = UpdateManagedDataMetadata(report.ManagedDataMigrationCandidates);
            if (result.MovedCount > 0 || metadataUpdates > 0)
            {
                await SaveConfigurationImmediatelyAsync();
            }

            result.ChangedCount += metadataUpdates;
            return result;
        }

        private int UpdateManagedDataMetadata(IEnumerable<ManagedDataMigrationCandidate> candidates)
        {
            var changed = 0;

            foreach (var candidate in candidates)
            {
                if (!PathExists(candidate.TargetPath) || PathExists(candidate.SourcePath))
                {
                    continue;
                }

                foreach (var addon in configuration.AddonMetadata.Values)
                {
                    if (PathReferencesEqual(addon.FolderPath, candidate.SourcePath))
                    {
                        addon.FolderPath = candidate.TargetPath;
                        addon.IsGmaFile = !candidate.IsDirectory &&
                                          candidate.TargetPath.EndsWith(".gma", StringComparison.OrdinalIgnoreCase);
                        changed++;
                    }

                    if (PathReferencesEqual(addon.LocalManagedPath, candidate.SourcePath))
                    {
                        addon.LocalManagedPath = candidate.TargetPath;
                        changed++;
                    }
                }
            }

            return changed;
        }

        private static bool PathExists(string? path)
        {
            return !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path));
        }

        private static bool PathReferencesEqual(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            try
            {
                return string.Equals(NormalizeLocalPath(left), NormalizeLocalPath(right), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
            }
        }

        private List<string> GetWorkshopDirectoriesOrEmpty(string searchPattern, string operationName)
        {
            if (string.IsNullOrWhiteSpace(workshopPath) || !Directory.Exists(workshopPath))
            {
                errorHandler.HandleWarning(
                    $"Workshop path does not exist; treating it as empty. Path: {workshopPath}",
                    operationName);
                return new List<string>();
            }

            try
            {
                return Directory.GetDirectories(workshopPath, searchPattern).ToList();
            }
            catch (DirectoryNotFoundException)
            {
                errorHandler.HandleWarning(
                    $"Workshop path disappeared during scan; treating it as empty. Path: {workshopPath}",
                    operationName);
                return new List<string>();
            }
        }


        public async Task InitializeAsync()
        {
            // Initializing Addon Manager
            try
            {
                if (eventLogger.IsExperimentContextActive && !eventLogger.Enabled)
                {
                    eventLogger.Enabled = true;
                }

                eventLogger.EnsureLogFileReady();

                if (Interlocked.Exchange(ref _sessionLogged, 1) == 0)
                {
                    LogExperimentEvent("SessionStart", eventScope: "system", result: "success");
                }

                if (modeStrategy.RequiresAdmin)
                {
                    junctionService.ValidateAdminPrivileges();
                }

                await modeStrategy.InitializeAsync(this);

                // 操作ログマネージャーを初期化
                operationLogManager = new OperationLogManager(managerPath);
                
                // 起動時に古いログをクリーンアップ
                operationLogManager.CleanupOldLogs();

                if (File.Exists(configPath) || File.Exists(configPath + ".bak"))
                {
                    await LoadConfigurationAsync();
                    
                    // ジャンクションアセットが存在しない場合は追加
                    if (DisableMode == DisableMode.Hard)
                    {
                        EnsureJunctionAssetExists();
                    }
                }
                else
                {
                    configuration = new Configuration();
                    configuration.CreateDefaultAssets(DisableMode == DisableMode.Hard);
                    await SaveConfigurationAsync();
                }

                RecordCurrentPathState();
                await SaveConfigurationAsync();
                
                // 起動時のシステム整合性チェック
                await CheckIncompleteOperationsAsync();

                await MigrateExistingAddonsAsync();
                
                // 起動時のシステム整合性チェック
                await modeStrategy.ValidateSystemIntegrityAsync(this);
                
                // ジャンクション状態のアドオンを検出して更新
                if (DisableMode == DisableMode.Hard)
                {
                    await UpdateJunctionAssetAsync();
                }
                
                // Subscribeアセットに全アドオンが含まれていることを確認
                await EnsureAllAddonsInSubscribeAssetAsync();

                // Read-only observation: seed a brand-new profile or reconcile
                // GMod-side transitions before any explicit GAM runtime write.
                await TryReconcileGmodDisabledAddonsFromRuntimeAsync(
                    "InitializeAsync");
                
                // 最後にサブスクライブ解除されたアドオンをクリーンアップ
                await modeStrategy.CleanupUnsubscribedAddonsAsync(this);

                await SaveConfigurationImmediatelyAsync();

                // Initialization owns this cleanup. Await it so callers can safely dispose or
                // replace the manager directory as soon as InitializeAsync completes.
                await CleanupStaleIconCacheAsync();
                _initializationCompleted = true;
                
                // Addon Manager initialization complete
            }
            catch (Exception)
            {
                throw; // エラーを再スローして、呼び出し元で処理できるようにする
            }
        }

        private async Task CleanupStaleIconCacheAsync()
        {
            try
            {
                var snapshot = GetWorkshopSnapshotFromCache();
                if (!snapshot.IsAuthoritative)
                {
                    errorHandler.HandleWarning(
                        "Steam subscription state is not authoritative; skipping stale icon cleanup.",
                        "InitializeAsync");
                    return;
                }

                var activeIds = new HashSet<string>(
                    snapshot.SubscribedIds,
                    StringComparer.Ordinal);
                await workshopIconResolver.CleanupStaleIconsAsync(activeIds, TimeSpan.FromDays(30));
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning($"Failed to cleanup icon cache: {ex.Message}", "InitializeAsync");
            }
        }

        internal void EnsureManagerDirectories()
        {
            if (!Directory.Exists(managerPath))
            {
                ValidatePath(managerPath, "managerPath");
                Directory.CreateDirectory(managerPath);
                var dirInfo = new DirectoryInfo(managerPath);
                dirInfo.Attributes |= FileAttributes.Hidden;
            }

            if (!Directory.Exists(addonsPath))
            {
                ValidatePath(addonsPath, "addonsPath");
                Directory.CreateDirectory(addonsPath);
            }
        }

        internal void EnsureDataDirectory()
        {
            if (!Directory.Exists(managerPath))
            {
                ValidatePath(managerPath, "managerPath");
                Directory.CreateDirectory(managerPath);
            }
        }

        internal void TryMigrateLegacyManagerData()
        {
            if (DisableMode != DisableMode.Soft)
            {
                return;
            }

            var legacyManagerPath = Path.Combine(workshopPath, ".addon-manager");
            if (!Directory.Exists(legacyManagerPath))
            {
                return;
            }

            var legacyConfigPath = Path.Combine(legacyManagerPath, "config.json");
            if (!File.Exists(configPath) && File.Exists(legacyConfigPath))
            {
                try
                {
                    ValidatePath(legacyConfigPath, "legacyConfigPath");
                    ValidatePath(configPath, "configPath");
                    File.Copy(legacyConfigPath, configPath, true);
                    errorHandler.HandleInfo("Migrated legacy config.json to AppData for soft mode", "InitializeAsync");
                }
                catch (Exception ex)
                {
                    errorHandler.HandleWarning($"Failed to migrate legacy config.json: {ex.Message}", "InitializeAsync");
                }
            }

            var legacyPendingPath = Path.Combine(legacyManagerPath, "pending.json");
            if (!File.Exists(pendingPath) && File.Exists(legacyPendingPath))
            {
                try
                {
                    ValidatePath(legacyPendingPath, "legacyPendingPath");
                    ValidatePath(pendingPath, "pendingPath");
                    File.Copy(legacyPendingPath, pendingPath, true);
                    errorHandler.HandleInfo("Migrated legacy pending.json to AppData for soft mode", "InitializeAsync");
                }
                catch (Exception ex)
                {
                    errorHandler.HandleWarning($"Failed to migrate legacy pending.json: {ex.Message}", "InitializeAsync");
                }
            }
        }

        internal void WarnIfCacheOnDifferentDrive()
        {
            if (!string.IsNullOrEmpty(gmodCachePath) && !AreSameDrive(workshopPath, gmodCachePath))
            {
                errorHandler.HandleWarning(
                    $"Workshop content is located at {workshopPath}, while the Garry's Mod cache is at {gmodCachePath}. " +
                    "These paths reside on different drives, so hard link optimisation will fall back to file copies. " +
                    "Move the Steam library so both folders share the same volume to keep a single physical copy of each addon.",
                    "InitializeAsync");
            }
        }

        internal void EnsureCacheManagerDirectories()
        {
            if (string.IsNullOrEmpty(gmodCachePath))
            {
                return;
            }

            if (string.IsNullOrEmpty(gmodCacheManagerPath) || string.IsNullOrEmpty(gmodCacheAddonsPath))
            {
                return;
            }

            try
            {
                if (!Directory.Exists(gmodCachePath))
                {
                    errorHandler.HandleError(
                        new DirectoryNotFoundException($"Gmod cache path not found: {gmodCachePath}"),
                        "Cache path not found",
                        ErrorSeverity.Warning
                    );
                    gmodCacheManagerPath = null;
                    gmodCacheAddonsPath = null;
                    return;
                }

                if (!Directory.Exists(gmodCacheManagerPath))
                {
                    ValidatePath(gmodCacheManagerPath, "gmodCacheManagerPath");
                    Directory.CreateDirectory(gmodCacheManagerPath);
                    var dirInfo = new DirectoryInfo(gmodCacheManagerPath);
                    dirInfo.Attributes |= FileAttributes.Hidden;
                    errorHandler.HandleInfo($"Created cache manager directory: {gmodCacheManagerPath}", "InitializeAsync");
                }

                if (!Directory.Exists(gmodCacheAddonsPath))
                {
                    ValidatePath(gmodCacheAddonsPath, "gmodCacheAddonsPath");
                    Directory.CreateDirectory(gmodCacheAddonsPath);
                    errorHandler.HandleInfo($"Created cache addons directory: {gmodCacheAddonsPath}", "InitializeAsync");
                }
            }
            catch (Exception ex)
            {
                errorHandler.HandleError(ex, "Failed to create cache directories", ErrorSeverity.Error);
                gmodCacheManagerPath = null;
                gmodCacheAddonsPath = null;
            }
        }

        internal Task ValidateSystemIntegrityHardAsync()
        {
            return ValidateSystemIntegrityAsync();
        }

        internal Task CleanupUnsubscribedAddonsHardAsync()
        {
            return CleanupUnsubscribedAddonsAsync();
        }

        private async Task EnsureAllAddonsInSubscribeAssetAsync()
        {
            var subscribeAsset = configuration.Assets.FirstOrDefault(a => a.Id == SubscribeSystemAssetId);
            if (subscribeAsset == null)
            {
                subscribeAsset = new Asset("Subscribe Asset", true)
                {
                    Id = SubscribeSystemAssetId
                };
                configuration.Assets.Insert(0, subscribeAsset);
            }

            var needsSave =
                subscribeAsset.Name != "Subscribe Asset" ||
                !subscribeAsset.IsSystem ||
                !subscribeAsset.ContainsAllAddons() ||
                subscribeAsset.Addons.Count != 1 ||
                subscribeAsset.AddonStates.Count > 0;

            subscribeAsset.Name = "Subscribe Asset";
            subscribeAsset.IsSystem = true;
            subscribeAsset.IsFavorite = false;
            subscribeAsset.SetAllAddons();
            subscribeAsset.AddonStates.Clear();

            var snapshot = GetWorkshopSnapshotFromCache();
            var observation = subscriptionObservationService.Observe(configuration, snapshot);
            PendingDownloadCount = observation.PendingDownloadCount;
            needsSave |= observation.Changed;

            if (needsSave)
            {
                await SaveConfigurationAsync();
            }

            await ApplyPendingNewSubscriptionStatesAsync(
                snapshot,
                observation,
                "EnsureAllAddonsInSubscribeAsset");
        }

        public async Task<bool> RefreshGmodDisabledAddonsFromRuntimeAsync()
        {
            await _runtimeReconcileGate.WaitAsync();
            try
            {
                var result = await TryReconcileGmodDisabledAddonsFromRuntimeAsync(
                    "RefreshGmodDisabledAddonsFromRuntime");
                return result?.MembershipChanged ?? false;
            }
            finally
            {
                _runtimeReconcileGate.Release();
            }
        }

        internal async Task<GmodDisabledAddonReconciliationResult?>
            RefreshGmodDisabledAddonsBeforePendingApplyAsync()
        {
            await _runtimeReconcileGate.WaitAsync();
            try
            {
                var result = await TryReconcileGmodDisabledAddonsFromRuntimeAsync(
                    "PendingChangeManager.BeforeRuntimeApply",
                    runtimeApplyAlreadyQueued: true);
                return result;
            }
            finally
            {
                _runtimeReconcileGate.Release();
            }
        }

        private async Task<GmodDisabledAddonReconciliationResult?> TryReconcileGmodDisabledAddonsFromRuntimeAsync(
            string operationName,
            ISet<string>? authoritativeSubscribedIds = null,
            bool runtimeApplyAlreadyQueued = false)
        {
            var workshopSnapshot = GetWorkshopSnapshotFromCache();
            PendingDownloadCount = workshopSnapshot.SubscribedIds
                .Except(workshopSnapshot.InstalledIds, StringComparer.Ordinal)
                .Count();

            var subscribedIds = authoritativeSubscribedIds;
            if (subscribedIds == null && workshopSnapshot.IsAuthoritative)
            {
                subscribedIds = new HashSet<string>(
                    workshopSnapshot.SubscribedIds,
                    StringComparer.Ordinal);
            }
            if (subscribedIds == null)
            {
                errorHandler.HandleWarning(
                    "Steam Workshop subscription state is not authoritative; GMod-side addon changes were not synchronized.",
                    operationName);
                return null;
            }

            if (gmodAddonStateStore == null)
            {
                errorHandler.HandleWarning(
                    "Garry's Mod settings path is unavailable; GMod-side addon changes were not synchronized.",
                    operationName);
                return null;
            }

            AddonMountSnapshot runtimeSnapshot;
            try
            {
                runtimeSnapshot = gmodAddonStateStore.ReadSnapshot();
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning(
                    $"Failed to read addonnomount.txt for GMod-side synchronization: {ex.Message}",
                    operationName);
                return null;
            }

            if (!runtimeSnapshot.IsValidFormat)
            {
                errorHandler.HandleWarning(
                    "addonnomount.txt is malformed; GMod-side addon changes and attribution baselines were left unchanged.",
                    operationName);
                return null;
            }

            var rollbackConfiguration = CloneConfiguration(configuration);
            var allowInitialSeed = !configuration.InitialRuntimeImportCompleted;
            var migrationDesiredStates = configuration.GmodAttributionMigrationPending
                ? BuildExpectedStatesForAssets(
                    configuration.Assets
                        .Where(asset => !GmodDisabledAddonReconciliationService
                            .IsProtectedSystemAsset(asset.Id))
                        .ToList(),
                    subscribedIds)
                : null;
            var result = gmodDisabledAddonReconciliationService.ReconcileValidObservation(
                configuration,
                subscribedIds,
                runtimeSnapshot.DisabledIds,
                runtimeSnapshot.ObservedAtUtc,
                allowInitialSeed,
                gmodAddonStateStore.NoMountFilePath,
                migrationDesiredStates);

            if (!result.Changed)
            {
                return result;
            }

            try
            {
                await SaveConfigurationImmediatelyAsync();
            }
            catch
            {
                configuration = rollbackConfiguration;
                throw;
            }

            if (result.PendingRecovery == PendingGamRuntimeWriteRecovery.NotApplied)
            {
                if (runtimeApplyAlreadyQueued)
                {
                    configuration.PendingGamRuntimeWrite = null;
                    await SaveConfigurationImmediatelyAsync();
                    runtimeReapplyRequiredFromRecovery = false;
                }
                else
                {
                    result.QueuedRuntimeApplyGeneration =
                        await TryQueueRecoveredRuntimeApplyAsync(operationName);
                }
            }

            if (result.PendingRecovery == PendingGamRuntimeWriteRecovery.Conflicted)
            {
                errorHandler.HandleWarning(
                    "A pending GAM runtime write conflicted with the current GMod state. Current GMod changes were preserved and no automatic overwrite was performed.",
                    operationName);
            }

            return result;
        }

        private async Task<Guid?> TryQueueRecoveredRuntimeApplyAsync(string operationName)
        {
            var queue = QueueRuntimeApplyProvider;
            var trackedQueue = QueueRuntimeApplyTrackedProvider;
            if (queue == null && trackedQueue == null)
            {
                runtimeReapplyRequiredFromRecovery = true;
                return null;
            }

            var pendingIntent = configuration.PendingGamRuntimeWrite;
            try
            {
                Guid? queuedGeneration = null;
                if (trackedQueue != null)
                {
                    queuedGeneration = trackedQueue.Invoke();
                }
                else
                {
                    queue!.Invoke();
                }
                configuration.PendingGamRuntimeWrite = null;
                await SaveConfigurationImmediatelyAsync();
                runtimeReapplyRequiredFromRecovery = false;
                return queuedGeneration;
            }
            catch (Exception ex)
            {
                configuration.PendingGamRuntimeWrite = pendingIntent;
                runtimeReapplyRequiredFromRecovery = true;
                errorHandler.HandleWarning(
                    $"Failed to durably queue recovered runtime apply: {ex.Message}",
                    operationName);
                return null;
            }
        }

        private async Task ApplyPendingNewSubscriptionStatesAsync(
            SteamWorkshopSnapshot snapshot,
            SubscriptionObservationResult observation,
            string operationName)
        {
            if (snapshot == null ||
                observation == null ||
                !snapshot.IsAuthoritative ||
                !observation.IsAuthoritative)
            {
                return;
            }

            var subscribedIds = new HashSet<string>(
                snapshot.SubscribedIds.Where(IsWorkshopNumericId),
                StringComparer.Ordinal);
            var newlyObservedIds = new HashSet<string>(
                observation.NewlySubscribedIds
                    .Where(IsWorkshopNumericId)
                    .Where(subscribedIds.Contains),
                StringComparer.Ordinal);
            if (GetPendingNewSubscriptionIds(subscribedIds, newlyObservedIds).Count == 0)
            {
                return;
            }

            await _runtimeReconcileGate.WaitAsync();
            try
            {
                var pendingIds = GetPendingNewSubscriptionIds(
                    subscribedIds,
                    newlyObservedIds);
                if (pendingIds.Count == 0)
                {
                    return;
                }

                // GMod remains authoritative for external transitions. Import
                // those transitions before calculating the new IDs' desired state,
                // then write only the newly subscribed scope below.
                var reconciliation =
                    await TryReconcileGmodDisabledAddonsFromRuntimeAsync(
                        operationName + ".ReconcileGmodState",
                        subscribedIds);
                if (reconciliation == null ||
                    reconciliation.PendingRecovery ==
                        PendingGamRuntimeWriteRecovery.NotApplied ||
                    reconciliation.PendingRecovery ==
                        PendingGamRuntimeWriteRecovery.Conflicted)
                {
                    return;
                }

                pendingIds = GetPendingNewSubscriptionIds(
                    subscribedIds,
                    newlyObservedIds);
                if (pendingIds.Count == 0)
                {
                    return;
                }

                var targetStates = pendingIds.ToDictionary(
                    addonId => addonId,
                    addonId => assetStateResolver.Resolve(
                        addonId,
                        configuration.Assets,
                        subscribedIds).DesiredEnabled,
                    StringComparer.Ordinal);

                if (IsGmodCurrentlyRunning())
                {
                    QueueNewSubscriptionRuntimeApply(operationName);
                    return;
                }

                if (ApplyAddonStateStoreBulk(
                        targetStates,
                        allowConflictSupersede: false))
                {
                    foreach (var targetState in targetStates)
                    {
                        if (configuration.AddonMetadata.TryGetValue(
                                targetState.Key,
                                out var metadata))
                        {
                            metadata.IsEnabled = targetState.Value;
                        }
                    }

                    // ApplyAddonStateStoreBulk durably saves the runtime
                    // attribution before returning. Persist the metadata cache
                    // changed above as a separate step so a restart does not
                    // reload stale enabled values for the new subscription.
                    await SaveConfigurationImmediatelyAsync();
                    runtimeApplyRequiredForNewSubscriptions = false;
                    return;
                }

                if (configuration.PendingGamRuntimeWrite != null)
                {
                    await TryQueueRecoveredRuntimeApplyAsync(operationName);
                }
                else
                {
                    QueueNewSubscriptionRuntimeApply(operationName);
                }
            }
            finally
            {
                _runtimeReconcileGate.Release();
            }
        }

        private List<string> GetPendingNewSubscriptionIds(
            ISet<string> subscribedIds,
            IEnumerable<string> newlyObservedIds)
        {
            var pendingIds = new HashSet<string>(
                newlyObservedIds ?? Enumerable.Empty<string>(),
                StringComparer.Ordinal);
            var appliedStates = configuration.LastGamAppliedAddonStates ??
                new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var addonId in
                     (configuration.SubscriptionFirstSeenAtUtc ??
                      new Dictionary<string, DateTime>()).Keys)
            {
                if (subscribedIds.Contains(addonId) &&
                    !appliedStates.ContainsKey(addonId))
                {
                    pendingIds.Add(addonId);
                }
            }

            pendingIds.IntersectWith(subscribedIds);
            return pendingIds
                .Where(IsWorkshopNumericId)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
        }

        private void QueueNewSubscriptionRuntimeApply(string operationName)
        {
            var queue = QueueRuntimeApplyProvider;
            if (queue == null)
            {
                runtimeApplyRequiredForNewSubscriptions = true;
                return;
            }

            try
            {
                queue.Invoke();
                runtimeApplyRequiredForNewSubscriptions = false;
            }
            catch (Exception ex)
            {
                runtimeApplyRequiredForNewSubscriptions = true;
                errorHandler.HandleWarning(
                    $"Failed to queue new-subscription runtime apply: {ex.Message}",
                    operationName);
            }
        }

        internal async Task<bool> FinalizeRuntimeAttributionConflictAsync(
            string? expectedOperationId)
        {
            await _runtimeReconcileGate.WaitAsync();
            try
            {
                return await FinalizeRuntimeAttributionConflictCoreAsync(
                    expectedOperationId);
            }
            finally
            {
                _runtimeReconcileGate.Release();
            }
        }

        internal void TryFinalizeOrphanedRuntimeAttributionConflict(
            bool noPendingMarkerDurablyConfirmed)
        {
            if (!noPendingMarkerDurablyConfirmed)
            {
                return;
            }

            _runtimeReconcileGate.Wait();
            try
            {
                var pending = configuration.PendingGamRuntimeWrite;
                if (pending?.ConflictDetected != true)
                {
                    return;
                }

                FinalizeRuntimeAttributionConflictSynchronouslyCore(
                    pending.OperationId);
            }
            finally
            {
                _runtimeReconcileGate.Release();
            }
        }

        private async Task<bool> FinalizeRuntimeAttributionConflictCoreAsync(
            string? expectedOperationId)
        {
            var pending = configuration.PendingGamRuntimeWrite;
            if (pending?.ConflictDetected != true ||
                string.IsNullOrWhiteSpace(expectedOperationId) ||
                !string.Equals(
                    pending.OperationId,
                    expectedOperationId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            configuration.PendingGamRuntimeWrite = null;
            try
            {
                await SaveConfigurationImmediatelyAsync();
                return true;
            }
            catch
            {
                configuration.PendingGamRuntimeWrite = pending;
                throw;
            }
        }

        private bool FinalizeRuntimeAttributionConflictSynchronouslyCore(
            string? expectedOperationId)
        {
            var pending = configuration.PendingGamRuntimeWrite;
            if (pending?.ConflictDetected != true ||
                string.IsNullOrWhiteSpace(expectedOperationId) ||
                !string.Equals(
                    pending.OperationId,
                    expectedOperationId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            configuration.PendingGamRuntimeWrite = null;
            try
            {
                SaveConfigurationImmediatelySynchronously();
                return true;
            }
            catch
            {
                configuration.PendingGamRuntimeWrite = pending;
                throw;
            }
        }

        public async Task MigrateExistingAddonsAsync()
        {
            await MigrateExistingAddonsAsync(null);
        }
        
        public Task MigrateExistingAddonsAsync(HashSet<string>? addonIdsToProcess)
        {
            return modeStrategy.MigrateExistingAddonsAsync(this, addonIdsToProcess);
        }

        internal async Task MigrateExistingAddonsHardAsync(HashSet<string>? addonIdsToProcess)
        {
            var directories = GetVisibleWorkshopDirectoriesOrEmpty("MigrateExistingAddons");

            foreach (var directory in directories)
            {
                string dirName = Path.GetFileName(directory);
                
                // Skip if we're only processing specific addon IDs and this isn't one of them
                if (addonIdsToProcess != null && !addonIdsToProcess.Contains(dirName))
                {
                    continue;
                }
                
                if (long.TryParse(dirName, out _))
                {
                    if (!DirectoryHasAddonPayload(directory, "MigrateExistingAddons"))
                    {
                        errorHandler.HandleInfo($"Skipping empty workshop directory: {directory}", "MigrateExistingAddons");
                        continue;
                    }

                    if (junctionService.IsJunction(directory))
                    {
                        // Skipping - already a junction
                        continue;
                    }

                    string targetPath = Path.Combine(addonsPath, dirName);
                    ValidatePath(targetPath, "targetPath");
                    bool targetAlreadyExists = Directory.Exists(targetPath);
                    string? tempPath = null;

                    try
                    {
                        // 実体フォルダの場合、管理フォルダに移動（失敗時はロールバック）
                        if (!targetAlreadyExists)
                        {
                            ValidatePath(directory, "directory");
                            await Task.Run(() => Directory.Move(directory, targetPath));
                        }
                        else
                        {
                            tempPath = directory + "_temp_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                            ValidatePath(directory, "directory");
                            ValidatePath(tempPath, "tempPath");
                            await Task.Run(() => Directory.Move(directory, tempPath));
                        }

                        bool workshopPresenceCreated = false;

                        // GMAファイルが存在するかチェック（存在する場合はハードリンク方式を優先）
                        string gmaPath = Path.Combine(targetPath, $"{dirName}.gma");
                        if (File.Exists(gmaPath))
                        {
                            try
                            {
                                junctionService.CreateWorkshopAddonStructure(workshopPath, dirName, gmaPath);
                                workshopPresenceCreated = true;
                                errorHandler.HandleInfo($"Migrated addon {dirName} to hard link system", "MigrateExistingAddons");
                            }
                            catch (Exception ex)
                            {
                                errorHandler.HandleError(ex,
                                    $"Failed to create hard link structure for addon {dirName}, falling back to junction",
                                    ErrorSeverity.Warning);
                            }
                        }

                        if (!workshopPresenceCreated)
                        {
                            try
                            {
                                CreateJunctionWithMetrics(directory, targetPath);
                                workshopPresenceCreated = true;
                            }
                            catch (IOException ex) when (ex.Message.Contains("already exists and is not a junction"))
                            {
                                HandleExistingDirectoryDuringMigration(directory, targetPath, dirName);
                                workshopPresenceCreated = true;
                            }
                        }

                        // 既に管理フォルダが存在していた場合のみ、残っている実体フォルダをマージ
                        if (!string.IsNullOrEmpty(tempPath) && Directory.Exists(tempPath))
                        {
                            try
                            {
                                await Task.Run(() =>
                                {
                                    MergeDirectories(tempPath, targetPath);
                                    Directory.Delete(tempPath, true);
                                });
                            }
                            catch (Exception ex)
                            {
                                // ロールバックは難しいので、テンポラリを残して警告に留める
                                errorHandler.HandleError(ex,
                                    $"Failed to merge addon {dirName} contents into managed folder. Leaving temp folder: {tempPath}",
                                    ErrorSeverity.Warning);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        await RollbackFolderMigrationAsync(directory, targetPath, tempPath, targetAlreadyExists);

                        if (ex is Win32Exception win32 && win32.NativeErrorCode == 4392)
                        {
                            errorHandler.HandleError(ex,
                                $"Failed to migrate addon {dirName} due to invalid reparse data (Win32=4392). Migration was rolled back.",
                                ErrorSeverity.Warning);
                        }
                        else
                        {
                            errorHandler.HandleError(ex,
                                $"Failed to migrate addon {dirName}. Migration was rolled back.",
                                ErrorSeverity.Warning);
                        }
                        continue;
                    }

                    if (!configuration.AddonMetadata.ContainsKey(dirName))
                    {
                        var addon = await ScanAddonAsync(targetPath);
                        if (addon != null)
                        {
                            configuration.AddonMetadata[dirName] = addon;
                        }
                    }
                }
            }

            // Scan cache folder for .gma files and migrate to managed folder
            if (!string.IsNullOrEmpty(gmodCachePath))
            {
                // フォルダが存在しない場合は作成を試みる
                if (!string.IsNullOrEmpty(gmodCacheAddonsPath) && !Directory.Exists(gmodCacheAddonsPath))
                {
                    try
                    {
                        ValidatePath(gmodCacheManagerPath, "gmodCacheManagerPath");
                        ValidatePath(gmodCacheAddonsPath, "gmodCacheAddonsPath");
                        Directory.CreateDirectory(gmodCacheManagerPath);
                        Directory.CreateDirectory(gmodCacheAddonsPath);
                        errorHandler.HandleInfo("Created missing cache management folders during migration", "MigrateExistingAddons");
                    }
                    catch (Exception ex)
                    {
                        errorHandler.HandleError(ex, "Failed to create cache folders during migration", ErrorSeverity.Error);
                        return; // GMA移行をスキップ
                    }
                }

                // フォルダが正常に存在する場合のみ処理を続行
                if (Directory.Exists(gmodCachePath) && Directory.Exists(gmodCacheAddonsPath))
                {
                    var gmaFiles = Directory.GetFiles(gmodCachePath, "*.gma");
                    foreach (var gmaFile in gmaFiles)
                    {
                    string fileName = Path.GetFileNameWithoutExtension(gmaFile);
                    
                        // Add or update metadata
                    if (addonIdsToProcess != null && !addonIdsToProcess.Contains(fileName))
                    {
                        continue;
                    }
                    
                    // Extract workshop ID from filename
                    if (long.TryParse(fileName, out _))
                    {
                        string targetPath = Path.Combine(gmodCacheAddonsPath, Path.GetFileName(gmaFile));
                        ValidatePath(targetPath, "targetPath");
                        
                        // Move file to managed folder
                        if (!File.Exists(targetPath))
                        {
                            ValidatePath(gmaFile, "gmaFile");
                            File.Move(gmaFile, targetPath);
                        }
                        else
                        {
                            // If already exists in managed folder, delete the cache one
                            ValidatePath(gmaFile, "gmaFile");
                            File.Delete(gmaFile);
                        }
                        
                        // Create hard link back to cache (keep it enabled by default)
                        if (AreSameDrive(targetPath, gmaFile))
                        {
                            CreateHardLinkSafe(gmaFile, targetPath);
                        }
                        else
                        {
                            // Different drives - copy back
                            CopyFileForLinkFallback(fileName, targetPath, gmaFile, "MigrateExistingAddons");
                        }
                        
                        // Add or update metadata
                        if (!configuration.AddonMetadata.ContainsKey(fileName))
                        {
                            var addon = new WorkshopAddon
                            {
                                Id = fileName,
                                Title = fileName,
                                Size = new FileInfo(targetPath).Length,
                                LastUpdated = File.GetLastWriteTime(targetPath),
                                IsGmaFile = true,
                                IsEnabled = true
                            };
                            
                            // Try to read metadata from GMA file
                            ReadGmaMetadata(targetPath, addon);
                            
                            // タイトルが取得できなかった場合、複数回再試行
                            int retryCount = 0;
                            while ((string.IsNullOrWhiteSpace(addon.Title) || addon.Title == fileName || addon.Title.StartsWith("Workshop-")) && retryCount < 3)
                            {
                                await Task.Delay(100 * (retryCount + 1)); // 徐々に遅延を増やす
                                var title = await ReadGmaTitleOnlyAsync(targetPath);
                                if (!string.IsNullOrWhiteSpace(title) && title != fileName && !title.StartsWith("Workshop-"))
                                {
                                    addon.Title = title;
                                    break;
                                }
                                retryCount++;
                            }
                            
                            // それでも取得できない場合は、Workshop形式のタイトルを維持
                            if (string.IsNullOrWhiteSpace(addon.Title) || addon.Title == fileName)
                            {
                                addon.Title = $"Workshop-{fileName}";
                                addon.NeedsTitleUpdate = true; // タイトル更新が必要なフラグを立てる
                                }
                            else if (addon.Title.StartsWith("Workshop-"))
                            {
                                // すでにWorkshop形式の場合もタイトル更新が必要
                                addon.NeedsTitleUpdate = true;
                            }
                            
                            configuration.AddonMetadata[fileName] = addon;
                            
                            // Add to Subscribe asset
                            var subscribeAsset = configuration.Assets.FirstOrDefault(a => a.Id == "subscribe-system-asset");
                            if (subscribeAsset != null && !subscribeAsset.Addons.Contains(fileName))
                            {
                                subscribeAsset.AddAddon(fileName, AddonState.Enabled);
                            }
                        }
                        else
                        {
                            var existingAddon = configuration.AddonMetadata[fileName];
                            existingAddon.IsGmaFile = true;
                            existingAddon.IsEnabled = true;
                            
                            // 既存アドオンでもタイトルが不適切な場合は更新
                            if (existingAddon.Title == fileName || existingAddon.Title.StartsWith("Workshop-") || existingAddon.Title.StartsWith("Cache Addon") || existingAddon.NeedsTitleUpdate)
                            {
                                // 複数回再試行
                                int retryCount = 0;
                                bool titleUpdated = false;
                                while (!titleUpdated && retryCount < 3)
                                {
                                    await Task.Delay(100 * (retryCount + 1));
                                    var title = await ReadGmaTitleOnlyAsync(targetPath);
                                    if (!string.IsNullOrEmpty(title) && title != fileName && !title.StartsWith("Workshop-"))
                                    {
                                        existingAddon.Title = title;
                                        existingAddon.NeedsTitleUpdate = false;
                                        titleUpdated = true;
                                        break;
                                    }
                                    retryCount++;
                                }
                                
                                // それでも取得できない場合
                                if (!titleUpdated)
                                {
                                    if (existingAddon.Title == fileName || existingAddon.Title.StartsWith("Cache Addon"))
                                    {
                                        existingAddon.Title = $"Workshop-{fileName}";
                                    }
                                    existingAddon.NeedsTitleUpdate = true;
                                }
                            }
                            
                            // Add to Subscribe asset if not already there
                            var subscribeAsset = configuration.Assets.FirstOrDefault(a => a.Id == "subscribe-system-asset");
                            if (subscribeAsset != null && !subscribeAsset.Addons.Contains(fileName))
                            {
                                subscribeAsset.AddAddon(fileName, AddonState.Enabled);
                            }
                        }
                    }
                    }
                }
                else
                {
                    errorHandler.HandleWarning("Cache addon management folder not available, skipping GMA migration", "MigrateExistingAddons");
                }
            }

            await SaveConfigurationAsync();
        }

        private async Task RollbackFolderMigrationAsync(string workshopAddonPath, string managedAddonPath, string? tempPath, bool managedAlreadyExisted)
        {
            try
            {
                // Remove any partially created workshop presence so we can restore original content.
                CleanupWorkshopPathForRollback(workshopAddonPath);

                if (!string.IsNullOrEmpty(tempPath) && Directory.Exists(tempPath))
                {
                    EnsureWorkshopPathAvailableForRestore(workshopAddonPath);
                    if (!Directory.Exists(workshopAddonPath))
                    {
                        await Task.Run(() => Directory.Move(tempPath, workshopAddonPath));
                    }
                    return;
                }

                if (!managedAlreadyExisted && Directory.Exists(managedAddonPath))
                {
                    EnsureWorkshopPathAvailableForRestore(workshopAddonPath);
                    if (!Directory.Exists(workshopAddonPath))
                    {
                        await Task.Run(() => Directory.Move(managedAddonPath, workshopAddonPath));
                    }
                }
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning($"Rollback failed: {ex.Message}", "MigrateExistingAddons");
            }
        }

        private void CleanupWorkshopPathForRollback(string workshopAddonPath)
        {
            try
            {
                if (!Directory.Exists(workshopAddonPath))
                {
                    return;
                }

                if (junctionService.IsJunction(workshopAddonPath))
                {
                    junctionService.RemoveJunction(workshopAddonPath);
                    return;
                }

                Directory.Delete(workshopAddonPath, true);
            }
            catch (Exception ex)
            {
                // Best-effort cleanup; if this fails, restore will attempt a safe rename.
                errorHandler.HandleWarning($"Failed to cleanup workshop path during rollback: {ex.Message}", "MigrateExistingAddons");
            }
        }

        private void EnsureWorkshopPathAvailableForRestore(string workshopAddonPath)
        {
            if (!Directory.Exists(workshopAddonPath))
            {
                return;
            }

            try
            {
                if (junctionService.IsJunction(workshopAddonPath))
                {
                    junctionService.RemoveJunction(workshopAddonPath);
                }
                else
                {
                    // If it's empty, delete; otherwise move aside to avoid data loss.
                    bool isEmpty = !Directory.EnumerateFileSystemEntries(workshopAddonPath).Any();
                    if (isEmpty)
                    {
                        Directory.Delete(workshopAddonPath, true);
                    }
                    else
                    {
                        var backupPath = workshopAddonPath + "_rollback_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                        Directory.Move(workshopAddonPath, backupPath);
                    }
                }
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning($"Failed to make workshop path available for restore: {ex.Message}", "MigrateExistingAddons");
            }
        }

        private List<WorkshopAddon>? TryGetScanCache()
        {
            if (_scanCacheTtl <= TimeSpan.Zero)
            {
                return null;
            }

            lock (_scanCacheLock)
            {
                if (_scanCache == null)
                {
                    return null;
                }

                var age = DateTime.UtcNow - _scanCacheTimestampUtc;
                if (age > _scanCacheTtl)
                {
                    return null;
                }

                return new List<WorkshopAddon>(_scanCache);
            }
        }

        private void StoreScanCache(List<WorkshopAddon> addons)
        {
            if (_scanCacheTtl <= TimeSpan.Zero)
            {
                return;
            }

            lock (_scanCacheLock)
            {
                _scanCache = new List<WorkshopAddon>(addons);
                _scanCacheTimestampUtc = DateTime.UtcNow;
            }
        }

        public void InvalidateWorkshopScanCache()
        {
            lock (_scanCacheLock)
            {
                _scanCache = null;
                _scanCacheTimestampUtc = DateTime.MinValue;
            }
        }

        public async Task<List<WorkshopAddon>> ScanWorkshopFolderAsync()
        {
            var cached = TryGetScanCache();
            if (cached != null)
            {
                return cached;
            }

            var result = await modeStrategy.ScanWorkshopFolderAsync(this);
            result = await FinalizeWorkshopInventoryAsync(result);
            StoreScanCache(result);
            return result;
        }

        private async Task<List<WorkshopAddon>> FinalizeWorkshopInventoryAsync(
            List<WorkshopAddon> scannedAddons)
        {
            var snapshot = GetWorkshopSnapshotFromCache();
            var observation = subscriptionObservationService.Observe(configuration, snapshot);
            var changed = observation.Changed;

            if (!snapshot.IsAuthoritative)
            {
                PendingDownloadCount = 0;
                return scannedAddons;
            }

            var subscribedIds = new HashSet<string>(snapshot.SubscribedIds, StringComparer.Ordinal);
            var visibleAddons = scannedAddons
                .Where(addon => addon.IsLocal || subscribedIds.Contains(addon.Id))
                .GroupBy(addon => addon.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            var visibleWorkshopIds = new HashSet<string>(
                visibleAddons.Where(addon => !addon.IsLocal).Select(addon => addon.Id),
                StringComparer.Ordinal);

            PendingDownloadCount = subscribedIds.Except(visibleWorkshopIds, StringComparer.Ordinal).Count();

            foreach (var addon in visibleAddons.Where(addon => !addon.IsLocal))
            {
                addon.IsAvailable = true;
                addon.IsDownloadPending = false;
                if (configuration.SubscriptionFirstSeenAtUtc.TryGetValue(addon.Id, out var firstSeen))
                {
                    addon.FirstSeenSubscribedAtUtc = firstSeen;
                }
            }

            foreach (var asset in configuration.Assets.Where(asset => !IsSystemInventoryAsset(asset)))
            {
                var missingIds = asset.Addons
                    .Where(id => id != "*" && !subscribedIds.Contains(id))
                    .ToList();
                if (missingIds.Count == 0)
                {
                    continue;
                }

                if (configuration.RetainMissingAssetReferences)
                {
                    foreach (var addonId in missingIds)
                    {
                        if (configuration.AddonMetadata.TryGetValue(addonId, out var metadata))
                        {
                            if (metadata.IsAvailable || metadata.IsDownloadPending)
                            {
                                changed = true;
                            }
                            metadata.IsAvailable = false;
                            metadata.IsDownloadPending = false;
                        }
                        else
                        {
                            configuration.AddonMetadata[addonId] = new WorkshopAddon(addonId, string.Empty)
                            {
                                Title = $"Workshop-{addonId}",
                                NeedsTitleUpdate = true,
                                IsAvailable = false,
                                IsDownloadPending = false
                            };
                            changed = true;
                        }
                    }
                }
                else
                {
                    foreach (var addonId in missingIds)
                    {
                        asset.RemoveAddon(addonId);
                    }
                    changed = true;
                }
            }

            if (!configuration.RetainMissingAssetReferences)
            {
                var referencedIds = new HashSet<string>(
                    configuration.Assets
                        .Where(asset => !IsSystemInventoryAsset(asset))
                        .SelectMany(asset => asset.Addons)
                        .Where(id => id != "*"),
                    StringComparer.Ordinal);
                var staleMetadataIds = configuration.AddonMetadata
                    .Where(kvp => !kvp.Value.IsLocal &&
                                  !subscribedIds.Contains(kvp.Key) &&
                                  !referencedIds.Contains(kvp.Key))
                    .Select(kvp => kvp.Key)
                    .ToList();
                foreach (var addonId in staleMetadataIds)
                {
                    configuration.AddonMetadata.Remove(addonId);
                    changed = true;
                }
            }

            if (changed)
            {
                await SaveConfigurationImmediatelyAsync();
            }

            await ApplyPendingNewSubscriptionStatesAsync(
                snapshot,
                observation,
                "FinalizeWorkshopInventory");

            return visibleAddons;
        }

        internal async Task<List<WorkshopAddon>> ScanWorkshopFolderHardAsync()
        {
            var addons = new List<WorkshopAddon>();
            var processedIds = new HashSet<string>();
            var config = configuration ?? throw new InvalidOperationException("Configuration not initialized.");
            
            errorHandler.HandleInfo($"Starting ScanWorkshopFolderAsync - gmodCacheAddonsPath: {gmodCacheAddonsPath ?? "null"}", "ScanWorkshopFolderAsync");
            
            // 1. 管理フォルダのアドオンをスキャン
            if (Directory.Exists(addonsPath))
            {
                var directories = Directory.GetDirectories(addonsPath);
                
                foreach (var directory in directories)
                {
                    string addonId = Path.GetFileName(directory);
                    processedIds.Add(addonId);
                    
                    
                    // 保存されているメタデータがある場合は優先的に使用
                    if (config.AddonMetadata.ContainsKey(addonId))
                    {
                        var savedAddon = config.AddonMetadata[addonId];
                        // フォルダパスと有効状態を更新
                        savedAddon.FolderPath = directory;
                        // IsGmaFileはメタデータから保持するか、実際のファイルをチェック
                        if (!savedAddon.IsGmaFile)
                        {
                            savedAddon.IsGmaFile = IsGmaAddonRuntime(addonId);
                        }
                        string junctionPath = Path.Combine(workshopPath, addonId);
                        savedAddon.IsEnabled = Directory.Exists(junctionPath) && junctionService.IsJunction(junctionPath);
                        addons.Add(savedAddon);
                    }
                    else
                    {
                        // メタデータがない場合は新規スキャン
                        var addon = await ScanAddonAsync(directory);
                        if (addon != null)
                        {
                            // 実際のファイルをチェックしてGMAかどうか判定
                            addon.IsGmaFile = IsGmaAddonRuntime(addonId);
                            // 新しいアドオンのメタデータを保存
                            config.AddonMetadata[addonId] = addon;
                            addons.Add(addon);
                        }
                    }
                }
            }
            
            // 2. メタデータに保存されているが、まだ処理されていないアドオン（主にGMAファイル）を追加
            foreach (var kvp in config.AddonMetadata)
            {
                if (!processedIds.Contains(kvp.Key))
                {
                    // アドオンが実際に存在するか確認
                    bool addonExists = false;
                    
                    // GMAファイルの有効状態を更新
                    if (kvp.Value.IsGmaFile)
                    {
                        string? gmaPath = null;
                            
                            // 管理フォルダを確認
                            string managedGmaPath = Path.Combine(addonsPath, $"{kvp.Key}.gma");
                            if (File.Exists(managedGmaPath))
                            {
                                gmaPath = managedGmaPath;
                                addonExists = true;
                            }
                            
                            // キャッシュマネージャーパスを確認
                            if (!addonExists && !string.IsNullOrEmpty(gmodCacheAddonsPath))
                            {
                                gmaPath = Path.Combine(gmodCacheAddonsPath, $"{kvp.Key}.gma");
                                if (File.Exists(gmaPath))
                                {
                                    addonExists = true;
                                }
                                else
                                {
                                    // キャッシュパスを確認
                                    if (!string.IsNullOrEmpty(gmodCachePath))
                                    {
                                        gmaPath = Path.Combine(gmodCachePath, $"{kvp.Key}.gma");
                                        if (File.Exists(gmaPath))
                                        {
                                            addonExists = true;
                                        }
                                        else
                                        {
                                            gmaPath = null;
                                        }
                                    }
                                }
                            }
                            
                        kvp.Value.IsEnabled = !string.IsNullOrEmpty(gmaPath) && File.Exists(gmaPath);
                    }
                    else
                    {
                // メタデータがある場合は使用
                        string managedDirPath = Path.Combine(addonsPath, kvp.Key);
                        if (Directory.Exists(managedDirPath))
                        {
                            addonExists = true;
                        }
                    }
                    
                    // メタデータを更新して保存
                    if (addonExists)
                    {
                        addons.Add(kvp.Value);
                        processedIds.Add(kvp.Key);
                    }
                }
            }
            
            // 削除されたアドオンをリストから除外
            errorHandler.HandleInfo($"Cache scan check - gmodCacheAddonsPath: {gmodCacheAddonsPath ?? "null"}, Exists: {(!string.IsNullOrEmpty(gmodCacheAddonsPath) && Directory.Exists(gmodCacheAddonsPath))}", "ScanWorkshopFolderAsync");
            
            if (!string.IsNullOrEmpty(gmodCacheAddonsPath) && Directory.Exists(gmodCacheAddonsPath))
            {
                errorHandler.HandleInfo($"Scanning cache directory: {gmodCacheAddonsPath}", "ScanWorkshopFolderAsync");
                var gmaFiles = Directory.GetFiles(gmodCacheAddonsPath, "*.gma");
                
                foreach (var gmaFile in gmaFiles)
                {
                    string addonId = Path.GetFileNameWithoutExtension(gmaFile);
                    
                    // すでに処理済みの場合はスキップ
                    if (processedIds.Contains(addonId))
                        continue;
                        
                    processedIds.Add(addonId);
                    
                    // メタデータがある場合は使用
                    if (config.AddonMetadata.ContainsKey(addonId))
                    {
                        var savedAddon = config.AddonMetadata[addonId];
                        savedAddon.FolderPath = gmaFile;
                        savedAddon.IsGmaFile = true;  // 必ずGMAファイルとしてマーク
                        savedAddon.IsEnabled = File.Exists(gmaFile);
                        
                        // メタデータを更新して保存
                        config.AddonMetadata[addonId] = savedAddon;
                        
                        addons.Add(savedAddon);
                    }
                    else
                    {
                        // 新規GMAファイルの場合
                        var addon = new WorkshopAddon(addonId, gmaFile);
                        addon.IsGmaFile = true;
                        addon.IsEnabled = true;
                        
                        // GMAファイルからメタデータを読み取る
                        ReadGmaMetadata(gmaFile, addon);
                        
                        if (string.IsNullOrWhiteSpace(addon.Title))
                        {
                            addon.Title = $"Workshop-{addonId}";
                        }
                        
                        var fileInfo = new FileInfo(gmaFile);
                        addon.Size = fileInfo.Length;
                        addon.LastUpdated = fileInfo.LastWriteTimeUtc;
                        
                        // メタデータに保存
                        config.AddonMetadata[addonId] = addon;
                        
                        addons.Add(addon);
                    }
                }
                
                errorHandler.HandleInfo($"Found {gmaFiles.Length} GMA files in cache directory", "ScanWorkshopFolderAsync");
            }

            // 4. Workshopから削除されたアドオンのクリーンアップ
            var deletedAddonIds = await CleanupDeletedWorkshopAddonsAsync(addons);
            
            // 削除されたアドオンをリストから除外
            if (deletedAddonIds.Count > 0)
            {
                addons = addons.Where(a => !deletedAddonIds.Contains(a.Id)).ToList();
            }

            var localAddons = await ScanLocalAddonsAsync();
            if (localAddons.Count > 0)
            {
                var addonMap = addons.ToDictionary(a => a.Id, a => a, StringComparer.Ordinal);
                foreach (var localAddon in localAddons)
                {
                    addonMap[localAddon.Id] = localAddon;
                }
                addons = addonMap.Values.ToList();
            }

            var updatedFromCache = ApplyWorkshopCacheTags(addons);
            if (updatedFromCache)
            {
                await SaveConfigurationAsync();
            }
            
            return addons;
        }

        internal async Task<List<WorkshopAddon>> ScanWorkshopFolderSoftAsync()
        {
            var addons = new List<WorkshopAddon>();
            var processedIds = new HashSet<string>(StringComparer.Ordinal);
            var config = configuration ?? throw new InvalidOperationException("Configuration not initialized.");
            var disabledAddonIds = CaptureDisabledAddonIdsForScan();
            var invalidWorkshopIds = new List<string>();
            var emptyCachePlaceholderCount = 0;

            bool IsEnabledForScan(string addonId) =>
                disabledAddonIds == null || !disabledAddonIds.Contains(addonId);

            if (Directory.Exists(workshopPath))
            {
                var workshopDirs = GetVisibleWorkshopDirectoriesOrEmpty("ScanWorkshopFolderSoftAsync");
                var validWorkshopDirectories = new List<(string AddonId, string Directory)>();

                foreach (var directory in workshopDirs)
                {
                    var addonId = Path.GetFileName(directory);
                    if (!long.TryParse(addonId, out _))
                    {
                        continue;
                    }

                    if (!DirectoryHasAddonPayload(
                            directory,
                            "ScanWorkshopFolderSoftAsync",
                            logInvalidPayload: false))
                    {
                        invalidWorkshopIds.Add(addonId);
                        continue;
                    }

                    processedIds.Add(addonId);
                    validWorkshopDirectories.Add((addonId, directory));
                }

                using var scanSemaphore = new SemaphoreSlim(_maxParallelWorkshopScans);
                var scanTasks = validWorkshopDirectories.Select(async entry =>
                {
                    await scanSemaphore.WaitAsync();
                    try
                    {
                        if (config.AddonMetadata.TryGetValue(entry.AddonId, out var savedAddon))
                        {
                            savedAddon.FolderPath = entry.Directory;
                            savedAddon.IsGmaFile = IsGmaAddonRuntime(entry.AddonId);
                            savedAddon.IsEnabled = IsEnabledForScan(entry.AddonId);
                            try
                            {
                                var directoryInfo = new DirectoryInfo(entry.Directory);
                                var lastUpdatedUtc = directoryInfo.LastWriteTimeUtc;
                                if (savedAddon.Size <= 0 ||
                                    savedAddon.LastUpdated != lastUpdatedUtc)
                                {
                                    savedAddon.Size =
                                        await CalculateDirectorySizeAsync(directoryInfo);
                                    savedAddon.LastUpdated = lastUpdatedUtc;
                                }
                            }
                            catch (Exception ex)
                            {
                                errorHandler.HandleWarning(
                                    $"Failed to refresh addon size metadata for {entry.AddonId}: {ex.Message}",
                                    "ScanWorkshopFolderSoftAsync");
                            }

                            return (entry.AddonId, Addon: savedAddon, IsNew: false);
                        }

                        var addon = await ScanAddonAsyncCore(
                            entry.Directory,
                            payloadAlreadyValidated: true);
                        if (addon != null)
                        {
                            addon.IsGmaFile = IsGmaAddonRuntime(entry.AddonId);
                            addon.IsEnabled = IsEnabledForScan(entry.AddonId);
                        }

                        return (entry.AddonId, Addon: addon, IsNew: addon != null);
                    }
                    finally
                    {
                        scanSemaphore.Release();
                    }
                }).ToList();

                var scannedWorkshopAddons = await Task.WhenAll(scanTasks);
                foreach (var result in scannedWorkshopAddons)
                {
                    if (result.Addon == null)
                    {
                        continue;
                    }

                    if (result.IsNew)
                    {
                        config.AddonMetadata[result.AddonId] = result.Addon;
                    }

                    addons.Add(result.Addon);
                }
            }

            foreach (var kvp in config.AddonMetadata)
            {
                if (processedIds.Contains(kvp.Key))
                {
                    continue;
                }

                    bool addonExists = false;

                    if (kvp.Value.IsGmaFile)
                    {
                        string? gmaPath = null;

                        if (!string.IsNullOrEmpty(gmodCachePath))
                        {
                            gmaPath = Path.Combine(gmodCachePath, $"{kvp.Key}.gma");
                            if (HasNonEmptyFile(gmaPath))
                            {
                                addonExists = true;
                            }
                            else
                            {
                                gmaPath = null;
                            }
                        }

                        if (!addonExists)
                        {
                        var workshopDirPath = Path.Combine(workshopPath, kvp.Key);
                        var workshopGmaPath = Path.Combine(workshopDirPath, $"{kvp.Key}.gma");
                        if (HasNonEmptyFile(workshopGmaPath))
                            {
                                gmaPath = workshopGmaPath;
                                addonExists = true;
                            }
                        }

                        kvp.Value.IsEnabled = IsEnabledForScan(kvp.Key);
                        if (addonExists && gmaPath != null)
                        {
                            kvp.Value.FolderPath = gmaPath;
                        }
                    }
                    else
                    {
                        var workshopDirPath = Path.Combine(workshopPath, kvp.Key);
                        if (DirectoryHasAddonPayload(
                                workshopDirPath,
                                "ScanWorkshopFolderSoftAsync",
                                logInvalidPayload: false))
                        {
                            addonExists = true;
                            kvp.Value.FolderPath = workshopDirPath;
                            kvp.Value.IsEnabled = IsEnabledForScan(kvp.Key);
                        }
                    }

                if (addonExists)
                {
                    addons.Add(kvp.Value);
                    processedIds.Add(kvp.Key);
                }
            }

            if (!string.IsNullOrEmpty(gmodCachePath) && Directory.Exists(gmodCachePath))
            {
                var gmaFiles = Directory.GetFiles(gmodCachePath, "*.gma");
                foreach (var gmaFile in gmaFiles)
                {
                    var addonId = Path.GetFileNameWithoutExtension(gmaFile);
                    if (!long.TryParse(addonId, out _))
                    {
                        continue;
                    }

                    if (!HasNonEmptyFile(gmaFile))
                    {
                        emptyCachePlaceholderCount++;
                        continue;
                    }

                    if (processedIds.Contains(addonId))
                    {
                        continue;
                    }

                    processedIds.Add(addonId);

                    if (configuration?.AddonMetadata != null && configuration.AddonMetadata.ContainsKey(addonId))
                    {
                        var savedAddon = configuration.AddonMetadata[addonId];
                        savedAddon.FolderPath = gmaFile;
                        savedAddon.IsGmaFile = true;
                        savedAddon.IsEnabled = IsEnabledForScan(addonId);
                        var fileInfo = new FileInfo(gmaFile);
                        savedAddon.Size = fileInfo.Length;
                        savedAddon.LastUpdated = fileInfo.LastWriteTimeUtc;
                        configuration.AddonMetadata[addonId] = savedAddon;
                        addons.Add(savedAddon);
                    }
                    else
                    {
                        var addon = new WorkshopAddon(addonId, gmaFile)
                        {
                            IsGmaFile = true,
                            IsEnabled = IsEnabledForScan(addonId)
                        };

                        ReadGmaMetadata(gmaFile, addon);

                        if (string.IsNullOrWhiteSpace(addon.Title))
                        {
                            addon.Title = $"Workshop-{addonId}";
                        }

                        var fileInfo = new FileInfo(gmaFile);
                        addon.Size = fileInfo.Length;
                        addon.LastUpdated = fileInfo.LastWriteTimeUtc;

                        if (configuration != null)
                        {
                            configuration.AddonMetadata[addonId] = addon;
                        }

                        addons.Add(addon);
                    }
                }
            }

            if (invalidWorkshopIds.Count > 0)
            {
                errorHandler.HandleInfo(
                    $"Skipped {invalidWorkshopIds.Count} empty or invalid Workshop folders. " +
                    $"Example IDs: {string.Join(", ", invalidWorkshopIds.Take(5))}",
                    "ScanWorkshopFolderSoftAsync");
            }

            if (emptyCachePlaceholderCount > 0)
            {
                errorHandler.HandleInfo(
                    $"Skipped {emptyCachePlaceholderCount} empty GMA cache placeholders.",
                    "ScanWorkshopFolderSoftAsync");
            }

            var deletedAddonIds = await CleanupDeletedWorkshopAddonsAsync(addons);
            if (deletedAddonIds.Count > 0)
            {
                addons = addons.Where(a => !deletedAddonIds.Contains(a.Id)).ToList();
            }

            var localAddons = await ScanLocalAddonsAsync();
            if (localAddons.Count > 0)
            {
                var addonMap = addons.ToDictionary(a => a.Id, a => a, StringComparer.Ordinal);
                foreach (var localAddon in localAddons)
                {
                    addonMap[localAddon.Id] = localAddon;
                }
                addons = addonMap.Values.ToList();
            }

            await EnsureAllAddonsInSubscribeAssetAsync();

            var updatedFromCache = ApplyWorkshopCacheTags(addons);
            if (updatedFromCache)
            {
                await SaveConfigurationAsync();
            }

            return addons;
        }

        private async Task<List<WorkshopAddon>> ScanLocalAddonsAsync()
        {
            var results = new List<WorkshopAddon>();
            if (!EnableLocalAddonManagement)
            {
                return results;
            }

            if (string.IsNullOrWhiteSpace(localAddonsPath))
            {
                errorHandler.HandleWarning("Local addon paths are unavailable; skipping local addon scan.", "ScanLocalAddons");
                return results;
            }

            if (!Directory.Exists(localAddonsPath))
            {
                return results;
            }

            var mountEntries = new List<(string path, bool isGma)>();
            foreach (var dir in Directory.GetDirectories(localAddonsPath))
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(name) || name.StartsWith(".", StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(name, "gma", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                mountEntries.Add((dir, false));
            }

            var gmaPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddGmaFiles(localAddonsPath, gmaPaths);
            AddGmaFiles(localGmaAddonsPath, gmaPaths);
            AddGmaFiles(localRootGmaPath, gmaPaths);

            foreach (var gmaPath in gmaPaths)
            {
                mountEntries.Add((gmaPath, true));
            }

            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in mountEntries)
            {
                var addon = await BuildLocalAddonAsync(entry.path, entry.isGma);
                if (addon != null)
                {
                    results.Add(addon);
                    seenIds.Add(addon.Id);
                }
            }

            var removedLocalIds = new List<string>();
            foreach (var kvp in configuration.AddonMetadata.Where(kvp => kvp.Value.IsLocal).ToList())
            {
                if (seenIds.Contains(kvp.Key))
                {
                    continue;
                }

                var addon = kvp.Value;
                if (LocalAddonExistsOnDisk(addon))
                {
                    addon.IsEnabled = IsLocalMountPresent(addon);
                    addon.FolderPath = ResolveLocalDataPath(addon) ?? addon.FolderPath;
                    results.Add(addon);
                    seenIds.Add(addon.Id);
                }
                else
                {
                    removedLocalIds.Add(kvp.Key);
                }
            }

            if (removedLocalIds.Count > 0)
            {
                foreach (var addonId in removedLocalIds)
                {
                    configuration.AddonMetadata.Remove(addonId);
                    foreach (var asset in configuration.Assets)
                    {
                        if (asset.ContainsAllAddons() || asset.Addons.Contains(addonId))
                        {
                            asset.RemoveAddon(addonId);
                            if (asset.AddonStates.ContainsKey(addonId))
                            {
                                asset.AddonStates.Remove(addonId);
                            }
                        }
                    }
                    configuration.JunctionHistory.Remove(addonId);
                }

                await SaveConfigurationAsync();
            }

            return results;
        }

        private void AddGmaFiles(string? directory, HashSet<string> target)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(directory, "*.gma"))
            {
                target.Add(file);
            }
        }

        private async Task<WorkshopAddon?> BuildLocalAddonAsync(string mountPath, bool isGma)
        {
            if (string.IsNullOrWhiteSpace(mountPath))
            {
                return null;
            }

            var normalizedMount = NormalizeLocalPath(mountPath);
            var addonId = GetLocalAddonIdFromPath(normalizedMount);

            if (!configuration.AddonMetadata.TryGetValue(addonId, out var addon))
            {
                addon = new WorkshopAddon(addonId, normalizedMount);
            }

            addon.IsLocal = true;
            addon.IsGmaFile = isGma;
            addon.LocalMountPath = normalizedMount;
            addon.NeedsTitleUpdate = false;

            if (string.IsNullOrWhiteSpace(addon.LocalManagedPath))
            {
                addon.LocalManagedPath = GetDefaultLocalManagedPath(addonId, isGma);
            }

            addon.IsEnabled = isGma ? File.Exists(normalizedMount) : Directory.Exists(normalizedMount);

            var dataPath = ResolveLocalDataPath(addon) ?? normalizedMount;
            addon.FolderPath = dataPath;

            if (isGma)
            {
                if (File.Exists(dataPath))
                {
                    var fileInfo = new FileInfo(dataPath);
                    addon.Size = fileInfo.Length;
                    addon.LastUpdated = fileInfo.LastWriteTimeUtc;
                    ReadGmaMetadata(dataPath, addon);
                }
                else if (File.Exists(normalizedMount))
                {
                    var fileInfo = new FileInfo(normalizedMount);
                    addon.Size = fileInfo.Length;
                    addon.LastUpdated = fileInfo.LastWriteTimeUtc;
                    ReadGmaMetadata(normalizedMount, addon);
                }
            }
            else
            {
                if (Directory.Exists(dataPath))
                {
                    var dirInfo = new DirectoryInfo(dataPath);
                    addon.Size = await CalculateDirectorySizeAsync(dirInfo);
                    addon.LastUpdated = dirInfo.LastWriteTimeUtc;
                    PopulateLocalAddonMetadata(dataPath, addon);
                }
                else if (Directory.Exists(normalizedMount))
                {
                    var dirInfo = new DirectoryInfo(normalizedMount);
                    addon.Size = await CalculateDirectorySizeAsync(dirInfo);
                    addon.LastUpdated = dirInfo.LastWriteTimeUtc;
                    PopulateLocalAddonMetadata(normalizedMount, addon);
                }
            }

            if (string.IsNullOrWhiteSpace(addon.Title))
            {
                addon.Title = isGma
                    ? Path.GetFileNameWithoutExtension(normalizedMount)
                    : Path.GetFileName(normalizedMount);
            }

            configuration.AddonMetadata[addonId] = addon;
            return addon;
        }

        private void PopulateLocalAddonMetadata(string folderPath, WorkshopAddon addon)
        {
            if (!Directory.Exists(folderPath))
            {
                return;
            }

            ReadLocalAddonJson(folderPath, addon);

            if (string.IsNullOrWhiteSpace(addon.Title))
            {
                var gmaFiles = Directory.GetFiles(folderPath, "*.gma");
                if (gmaFiles.Length > 0)
                {
                    ReadGmaMetadata(gmaFiles[0], addon);
                }
            }
        }

        private void ReadLocalAddonJson(string folderPath, WorkshopAddon addon)
        {
            var jsonPath = Path.Combine(folderPath, "addon.json");
            if (!File.Exists(jsonPath))
            {
                return;
            }

            try
            {
                var json = File.ReadAllText(jsonPath);
                var obj = JObject.Parse(json);

                var title = obj.Value<string>("title") ?? obj.Value<string>("name");
                if (!string.IsNullOrWhiteSpace(title))
                {
                    addon.Title = title.Trim();
                }

                var type = obj.Value<string>("type");
                if (!string.IsNullOrWhiteSpace(type))
                {
                    addon.Type = type.Trim();
                }

                var description = obj.Value<string>("description");
                if (!string.IsNullOrWhiteSpace(description))
                {
                    addon.Description = description.Trim();
                }

                var author = obj.Value<string>("author") ?? obj.Value<string>("creator");
                if (!string.IsNullOrWhiteSpace(author))
                {
                    addon.Author = author.Trim();
                }

                var tagsToken = obj["tags"];
                if (tagsToken is JArray tagsArray)
                {
                    addon.Tags = tagsArray
                        .Select(t => t?.ToString())
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .Select(t => t!.Trim())
                        .ToArray();
                }
                else if (tagsToken is JValue tagsValue && !string.IsNullOrWhiteSpace(tagsValue.ToString()))
                {
                    addon.Tags = tagsValue.ToString()
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => t.Trim())
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .ToArray();
                }
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning($"Failed to read addon.json from {folderPath}: {ex.Message}", "ReadLocalAddonJson");
            }
        }

        private static string NormalizeLocalPath(string path)
        {
            var full = Path.GetFullPath(path);
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private string GetLocalAddonIdFromPath(string mountPath)
        {
            var normalized = NormalizeLocalPath(mountPath);
            foreach (var kvp in configuration.AddonMetadata)
            {
                var addon = kvp.Value;
                if (!addon.IsLocal || string.IsNullOrWhiteSpace(addon.LocalMountPath))
                {
                    continue;
                }

                if (string.Equals(NormalizeLocalPath(addon.LocalMountPath), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return kvp.Key;
                }
            }

            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(normalized.ToLowerInvariant());
            var hash = sha.ComputeHash(bytes);
            var hex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            return $"local_{hex.Substring(0, 12)}";
        }

        private string? GetDefaultLocalManagedPath(string addonId, bool isGma)
        {
            var root = EnsureLocalManagedRootDirectory();
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            return isGma
                ? Path.Combine(root, $"{addonId}.gma")
                : Path.Combine(root, addonId);
        }

        private string? EnsureLocalManagedRootDirectory()
        {
            if (string.IsNullOrWhiteSpace(localManagedRootPath))
            {
                return null;
            }

            try
            {
                if (!Directory.Exists(localManagedRootPath))
                {
                    ValidatePath(localManagedRootPath, "localManagedRootPath");
                    Directory.CreateDirectory(localManagedRootPath);
                }
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning($"Failed to create local addon manager directory: {ex.Message}", "LocalAddon");
                return null;
            }

            return localManagedRootPath;
        }

        private bool LocalAddonExistsOnDisk(WorkshopAddon addon)
        {
            var mountPath = ResolveLocalMountPath(addon);
            if (addon.IsGmaFile)
            {
                if (!string.IsNullOrWhiteSpace(mountPath) && File.Exists(mountPath))
                {
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(addon.LocalManagedPath) && File.Exists(addon.LocalManagedPath))
                {
                    return true;
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(mountPath) && Directory.Exists(mountPath))
                {
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(addon.LocalManagedPath) && Directory.Exists(addon.LocalManagedPath))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsLocalMountPresent(WorkshopAddon addon)
        {
            var mountPath = ResolveLocalMountPath(addon);
            if (string.IsNullOrWhiteSpace(mountPath))
            {
                return false;
            }

            return addon.IsGmaFile ? File.Exists(mountPath) : Directory.Exists(mountPath);
        }

        private string? ResolveLocalMountPath(WorkshopAddon addon)
        {
            if (!string.IsNullOrWhiteSpace(addon.LocalMountPath))
            {
                return addon.LocalMountPath;
            }

            if (!string.IsNullOrWhiteSpace(addon.FolderPath))
            {
                var candidate = NormalizeLocalPath(addon.FolderPath);
                if (!string.IsNullOrWhiteSpace(localAddonsPath) &&
                    candidate.StartsWith(NormalizeLocalPath(localAddonsPath), StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }

                if (!string.IsNullOrWhiteSpace(localRootGmaPath) &&
                    candidate.StartsWith(NormalizeLocalPath(localRootGmaPath), StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            return null;
        }

        private string? ResolveLocalDataPath(WorkshopAddon addon)
        {
            if (!string.IsNullOrWhiteSpace(addon.LocalManagedPath))
            {
                if (addon.IsGmaFile && File.Exists(addon.LocalManagedPath))
                {
                    return addon.LocalManagedPath;
                }

                if (!addon.IsGmaFile && Directory.Exists(addon.LocalManagedPath))
                {
                    return addon.LocalManagedPath;
                }
            }

            if (!string.IsNullOrWhiteSpace(addon.LocalMountPath))
            {
                if (addon.IsGmaFile && File.Exists(addon.LocalMountPath))
                {
                    return addon.LocalMountPath;
                }

                if (!addon.IsGmaFile && Directory.Exists(addon.LocalMountPath))
                {
                    return addon.LocalMountPath;
                }
            }

            return addon.LocalManagedPath ?? addon.LocalMountPath;
        }

        private bool TryHandleLocalAddonToggle(string addonId, bool enable)
        {
            if (!IsLocalAddonId(addonId))
            {
                return false;
            }

            if (!EnableLocalAddonManagement)
            {
                if (Interlocked.Exchange(ref _localManagementDisabledNoticeLogged, 1) == 0)
                {
                    errorHandler.HandleInfo(
                        "Local addon management is disabled. Enable 'Local addons (experimental)' in settings to use local ON/OFF.",
                        "LocalAddon");
                }
                return true;
            }

            if (!configuration.AddonMetadata.TryGetValue(addonId, out var addon))
            {
                errorHandler.HandleWarning($"Local addon metadata not found: {addonId}", "LocalAddon");
                return true;
            }

            try
            {
                if (enable)
                {
                    EnableLocalAddon(addon);
                }
                else
                {
                    DisableLocalAddon(addon);
                }
            }
            catch (Exception ex)
            {
                var strictFailure = FindStrictLinkModeException(ex);
                if (strictFailure != null)
                {
                    throw strictFailure;
                }

                errorHandler.HandleError(ex,
                    $"Failed to {(enable ? "enable" : "disable")} local addon {addonId}",
                    ErrorSeverity.Warning);
            }

            return true;
        }

        private void EnableLocalAddon(WorkshopAddon addon)
        {
            if (addon == null || !addon.IsLocal)
            {
                return;
            }

            var mountPath = ResolveLocalMountPath(addon);
            if (string.IsNullOrWhiteSpace(mountPath))
            {
                errorHandler.HandleWarning($"Local addon {addon.Id} has no mount path; cannot enable.", "LocalAddon");
                return;
            }

            mountPath = NormalizeLocalPath(mountPath);
            addon.LocalMountPath = mountPath;

            if (addon.IsGmaFile)
            {
                EnableLocalGmaAddon(addon, mountPath);
            }
            else
            {
                EnableLocalFolderAddon(addon, mountPath);
            }

            addon.IsEnabled = IsLocalMountPresent(addon);
            addon.FolderPath = ResolveLocalDataPath(addon) ?? addon.FolderPath;
        }

        private void DisableLocalAddon(WorkshopAddon addon)
        {
            if (addon == null || !addon.IsLocal)
            {
                return;
            }

            var mountPath = ResolveLocalMountPath(addon);
            if (string.IsNullOrWhiteSpace(mountPath))
            {
                errorHandler.HandleWarning($"Local addon {addon.Id} has no mount path; cannot disable.", "LocalAddon");
                return;
            }

            mountPath = NormalizeLocalPath(mountPath);
            addon.LocalMountPath = mountPath;

            if (addon.IsGmaFile)
            {
                DisableLocalGmaAddon(addon, mountPath);
            }
            else
            {
                DisableLocalFolderAddon(addon, mountPath);
            }

            addon.IsEnabled = IsLocalMountPresent(addon);
            addon.FolderPath = ResolveLocalDataPath(addon) ?? addon.FolderPath;
        }

        private void EnableLocalFolderAddon(WorkshopAddon addon, string mountPath)
        {
            if (Directory.Exists(mountPath))
            {
                return;
            }

            var managedPath = addon.LocalManagedPath ?? GetDefaultLocalManagedPath(addon.Id, false);
            if (string.IsNullOrWhiteSpace(managedPath) || !Directory.Exists(managedPath))
            {
                errorHandler.HandleWarning($"Local addon {addon.Id} data not found; cannot enable.", "LocalAddon");
                return;
            }

            EnsureLocalMountParentDirectory(mountPath);
            ValidatePath(mountPath, "localMountPath");
            ValidatePath(managedPath, "localManagedPath");

            CreateLocalJunction(mountPath, managedPath);
            addon.LocalManagedPath = managedPath;
        }

        private void DisableLocalFolderAddon(WorkshopAddon addon, string mountPath)
        {
            if (!Directory.Exists(mountPath))
            {
                return;
            }

            try
            {
                if (junctionService.IsJunction(mountPath))
                {
                    junctionService.RemoveJunction(mountPath);
                    return;
                }
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning($"Failed to inspect local junction for {addon.Id}: {ex.Message}", "LocalAddon");
            }

            var managedPath = addon.LocalManagedPath ?? GetDefaultLocalManagedPath(addon.Id, false);
            if (string.IsNullOrWhiteSpace(managedPath))
            {
                errorHandler.HandleWarning($"Local addon {addon.Id} manager path unavailable; cannot disable.", "LocalAddon");
                return;
            }

            var root = EnsureLocalManagedRootDirectory();
            if (string.IsNullOrWhiteSpace(root))
            {
                errorHandler.HandleWarning($"Local addon manager directory unavailable; cannot disable {addon.Id}.", "LocalAddon");
                return;
            }

            ValidatePath(mountPath, "localMountPath");
            ValidatePath(managedPath, "localManagedPath");

            if (Directory.Exists(managedPath))
            {
                MergeDirectories(mountPath, managedPath);
                Directory.Delete(mountPath, true);
            }
            else
            {
                MoveDirectoryWithFallback(addon.Id, mountPath, managedPath, "DisableLocalFolder");
            }

            addon.LocalManagedPath = managedPath;
        }

        private void EnableLocalGmaAddon(WorkshopAddon addon, string mountPath)
        {
            if (File.Exists(mountPath))
            {
                return;
            }

            var managedPath = addon.LocalManagedPath ?? GetDefaultLocalManagedPath(addon.Id, true);
            if (string.IsNullOrWhiteSpace(managedPath) || !File.Exists(managedPath))
            {
                errorHandler.HandleWarning($"Local addon {addon.Id} GMA not found; cannot enable.", "LocalAddon");
                return;
            }

            EnsureLocalMountParentDirectory(mountPath);
            ValidatePath(mountPath, "localMountPath");
            ValidatePath(managedPath, "localManagedPath");

            if (AreSameDrive(mountPath, managedPath))
            {
                if (!CreateHardLinkSafe(mountPath, managedPath))
                {
                    CopyFileForLinkFallback(addon.Id, managedPath, mountPath, "EnableLocalGma");
                }
            }
            else
            {
                CopyFileForLinkFallback(addon.Id, managedPath, mountPath, "EnableLocalGma");
            }

            addon.LocalManagedPath = managedPath;
        }

        private void DisableLocalGmaAddon(WorkshopAddon addon, string mountPath)
        {
            if (!File.Exists(mountPath))
            {
                return;
            }

            var managedPath = addon.LocalManagedPath ?? GetDefaultLocalManagedPath(addon.Id, true);
            if (string.IsNullOrWhiteSpace(managedPath))
            {
                errorHandler.HandleWarning($"Local addon {addon.Id} manager path unavailable; cannot disable.", "LocalAddon");
                return;
            }

            var root = EnsureLocalManagedRootDirectory();
            if (string.IsNullOrWhiteSpace(root))
            {
                errorHandler.HandleWarning($"Local addon manager directory unavailable; cannot disable {addon.Id}.", "LocalAddon");
                return;
            }

            ValidatePath(mountPath, "localMountPath");
            ValidatePath(managedPath, "localManagedPath");

            if (File.Exists(managedPath))
            {
                if (IsHardLink(mountPath, managedPath))
                {
                    File.Delete(mountPath);
                }
                else
                {
                    CopyFileForLinkFallback(addon.Id, mountPath, managedPath, "DisableLocalGma");
                    File.Delete(mountPath);
                }
            }
            else
            {
                MoveFileWithFallback(addon.Id, mountPath, managedPath, "DisableLocalGma");
            }

            addon.LocalManagedPath = managedPath;
        }

        private void EnsureLocalMountParentDirectory(string mountPath)
        {
            var parent = Path.GetDirectoryName(mountPath);
            if (string.IsNullOrWhiteSpace(parent))
            {
                return;
            }

            if (!Directory.Exists(parent))
            {
                ValidatePath(parent, "localMountParent");
                Directory.CreateDirectory(parent);
            }
        }

        private void CreateLocalJunction(string junctionPath, string targetPath)
        {
            try
            {
                if (Directory.Exists(junctionPath) && junctionService.IsJunction(junctionPath))
                {
                    var existingTarget = junctionService.GetJunctionTarget(junctionPath);
                    var normalizedTarget = Path.GetFullPath(targetPath);
                    if (string.Equals(existingTarget, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
            }
            catch
            {
                // Fall through and attempt creation; metrics will be recorded on success.
            }

            junctionService.CreateJunction(junctionPath, targetPath);
            linkMetricsContext.Value?.RecordJunction();
        }

        private void MoveDirectoryWithFallback(string addonId, string source, string destination, string context)
        {
            ValidatePath(source, "localMoveSource");
            ValidatePath(destination, "localMoveDestination");

            if (Directory.Exists(destination))
            {
                MergeDirectories(source, destination);
                Directory.Delete(source, true);
                return;
            }

            try
            {
                Directory.Move(source, destination);
            }
            catch (IOException)
            {
                CopyDirectory(addonId, source, destination, context);
                Directory.Delete(source, true);
            }
        }

        private void MoveFileWithFallback(string addonId, string source, string destination, string context)
        {
            ValidatePath(source, "localMoveSource");
            ValidatePath(destination, "localMoveDestination");

            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            try
            {
                File.Move(source, destination);
            }
            catch (IOException)
            {
                CopyFileForLinkFallback(addonId, source, destination, context);
                File.Delete(source);
            }
        }

        private void CopyDirectory(string addonId, string source, string destination, string context)
        {
            ValidatePath(source, "localCopySource");
            ValidatePath(destination, "localCopyDestination");

            if (!Directory.Exists(destination))
            {
                Directory.CreateDirectory(destination);
            }

            foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                var relativePath = dir.Substring(source.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var targetDir = Path.Combine(destination, relativePath);
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }
            }

            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var relativePath = file.Substring(source.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var destFile = Path.Combine(destination, relativePath);
                var destDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrWhiteSpace(destDir) && !Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                CopyFileForLinkFallback(addonId, file, destFile, context);
            }
        }
        
                // Check if we already know about this addon
        private List<string> GetSubscribedAddonIdsFromCache()
        {
            return GetWorkshopSnapshotFromCache().SubscribedIds.ToList();
        }

        private SteamWorkshopSnapshot GetWorkshopSnapshotFromCache()
        {
            return customWorkshopCacheFilePaths != null
                ? SteamWorkshopCacheReader.GetWorkshopSnapshot(customWorkshopCacheFilePaths)
                : SteamWorkshopCacheReader.GetWorkshopSnapshot();
        }

        private Dictionary<string, WorkshopItemInfo> GetAddonDetailsFromCache()
        {
            return customWorkshopCacheFilePaths != null
                ? SteamWorkshopCacheReader.GetAddonDetails(customWorkshopCacheFilePaths)
                : SteamWorkshopCacheReader.GetAddonDetails();
        }

        private bool TryGetSubscribedAddonIdSet(string operationName, out HashSet<string> subscribedAddonIds)
        {
            subscribedAddonIds = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var snapshot = GetWorkshopSnapshotFromCache();
                PendingDownloadCount = snapshot.SubscribedIds
                    .Except(snapshot.InstalledIds, StringComparer.Ordinal)
                    .Count();
                if (!snapshot.IsAuthoritative)
                {
                    return false;
                }

                subscribedAddonIds.UnionWith(snapshot.SubscribedIds);
                return true;
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning($"Failed to read Steam Workshop subscription cache: {ex.Message}", operationName);
                return false;
            }
        }

        private bool IsCurrentInventoryAddon(
            string addonId,
            HashSet<string>? subscribedAddonIds,
            bool subscriptionTruthAvailable,
            string operationName)
        {
            if (subscriptionTruthAvailable)
            {
                return subscribedAddonIds != null && subscribedAddonIds.Contains(addonId);
            }

            var workshopDirPath = Path.Combine(workshopPath, addonId);
            if (DirectoryHasAddonPayload(workshopDirPath, operationName))
            {
                return true;
            }

            var managedDirPath = Path.Combine(addonsPath, addonId);
            if (DirectoryHasAddonPayload(managedDirPath, operationName))
            {
                return true;
            }

            var managedGmaPath = Path.Combine(addonsPath, $"{addonId}.gma");
            if (File.Exists(managedGmaPath))
            {
                return true;
            }

            if (!subscriptionTruthAvailable && IsGmodCacheAddonFilePresent(addonId))
            {
                return true;
            }

            return false;
        }

        private bool IsGmodCacheAddonFilePresent(string addonId)
        {
            if (string.IsNullOrEmpty(gmodCachePath))
            {
                return false;
            }

            var cacheGmaPath = Path.Combine(gmodCachePath, $"{addonId}.gma");
            if (File.Exists(cacheGmaPath))
            {
                return true;
            }

            var cacheCachePath = Path.Combine(gmodCachePath, $"{addonId}.cache");
            return File.Exists(cacheCachePath) && LooksLikeGmaFile(cacheCachePath);
        }

        private bool RemoveAddonFromCurrentInventory(string addonId)
        {
            var changed = false;

            foreach (var asset in configuration.Assets.Where(IsSystemInventoryAsset))
            {
                if (asset.Addons.Contains(addonId) || asset.AddonStates.ContainsKey(addonId))
                {
                    asset.RemoveAddon(addonId);
                    changed = true;
                }
            }

            if (!IsReferencedByUserAsset(addonId))
            {
                changed |= configuration.AddonMetadata.Remove(addonId);
                changed |= configuration.JunctionHistory.Remove(addonId);
            }

            return changed;
        }

        private bool IsReferencedByUserAsset(string addonId)
        {
            return configuration.Assets.Any(asset =>
                !IsSystemInventoryAsset(asset) &&
                (asset.Addons.Contains(addonId) || asset.AddonStates.ContainsKey(addonId)));
        }

        private static bool IsSystemInventoryAsset(Asset asset)
        {
            return asset.IsSystem ||
                   string.Equals(asset.Id, SubscribeSystemAssetId, StringComparison.Ordinal) ||
                   string.Equals(asset.Id, JunctionSystemAssetId, StringComparison.Ordinal);
        }

        /// Scans for truly new addons in the workshop folder that haven't been migrated yet
        /// </summary>
        public async Task<List<WorkshopAddon>> ScanForNewAddonsAsync()
        {
            var newAddons = new List<WorkshopAddon>();
            var config = configuration ?? throw new InvalidOperationException("Configuration not initialized.");

            var workshopSnapshot = GetWorkshopSnapshotFromCache();
            var subscriptionTruthAvailable = workshopSnapshot.IsAuthoritative;
            var cachedAddonIds = new HashSet<string>(
                workshopSnapshot.SubscribedIds,
                StringComparer.Ordinal);
            var observation = subscriptionObservationService.Observe(config, workshopSnapshot);
            PendingDownloadCount = observation.PendingDownloadCount;
            if (subscriptionTruthAvailable && cachedAddonIds.Count > 0)
            {
                errorHandler.HandleInfo($"Found {cachedAddonIds.Count} addon IDs in Steam Workshop cache", "ScanForNewAddonsAsync");
            }
            
            // Scan actual workshop folder for directories
            var workshopDirs = GetVisibleWorkshopDirectoriesOrEmpty("ScanForNewAddonsAsync");
            var downloadedAddonIds = new HashSet<string>(StringComparer.Ordinal);
            
            foreach (var dir in workshopDirs)
            {
                string dirName = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(dirName))
                    continue;
                
                // Check if it's a valid addon ID
                if (!long.TryParse(dirName, out _))
                    continue;

                if (!DirectoryHasAddonPayload(dir, "ScanForNewAddonsAsync"))
                {
                    errorHandler?.HandleInfo($"Skipping empty workshop directory: {dir}", "ScanForNewAddonsAsync");
                    continue;
                }

                downloadedAddonIds.Add(dirName);
                    
                // Check if we already know about this addon
                if (config.AddonMetadata.ContainsKey(dirName))
                    continue;
                    
            // Check for addon IDs from Steam cache that don't have directories yet
                if (junctionService.IsJunction(dir))
                    continue;
                    
                // This is a new, unmanaged addon
                var addon = new WorkshopAddon
                {
                    Id = dirName,
                    Title = $"Workshop-{dirName}",
                    IsEnabled = true, // It's in the workshop folder, so it's enabled
                    IsGmaFile = IsGmaAddonRuntime(dirName), // 実際のファイルをチェック
                    IsAvailable = true,
                    IsDownloadPending = false,
                    FirstSeenSubscribedAtUtc =
                        config.SubscriptionFirstSeenAtUtc.TryGetValue(dirName, out var firstSeen)
                            ? firstSeen
                            : (DateTime?)null
                };
                
                // Try to get more info
                try
                {
                    var dirInfo = new DirectoryInfo(dir);
                    addon.Size = await CalculateDirectorySizeAsync(dirInfo);
                    addon.LastUpdated = dirInfo.LastWriteTimeUtc;
                    
                    // Look for GMA files to get metadata
                    var gmaFiles = Directory.GetFiles(dir, "*.gma");
                    if (gmaFiles.Length > 0)
                    {
                        ReadGmaMetadata(gmaFiles[0], addon);
                    }
                }
                catch (Exception)
                {
                    // Failed to read GMA metadata - continue with basic addon info
                    errorHandler?.HandleWarning($"Failed to read GMA metadata for addon {addon.Id}", "ReadGmaMetadata");
                }
                
                newAddons.Add(addon);
            }
            
            // Also scan cache folder for new GMA files
            if (!string.IsNullOrEmpty(gmodCachePath) && Directory.Exists(gmodCachePath))
            {
                var gmaFiles = Directory.GetFiles(gmodCachePath, "*.gma");
                
                foreach (var gmaFile in gmaFiles)
                {
                    string addonId = Path.GetFileNameWithoutExtension(gmaFile);
                    
                    // Check if it's a valid addon ID
                    if (!long.TryParse(addonId, out _))
                        continue;

                    if (subscriptionTruthAvailable && !cachedAddonIds.Contains(addonId))
                        continue;

                    downloadedAddonIds.Add(addonId);
                        
                    // Check if we already know about this addon
                    if (config.AddonMetadata.ContainsKey(addonId))
                        continue;
                        
                    // This is a new GMA addon
                    var addon = new WorkshopAddon
                    {
                        Id = addonId,
                        Title = addonId,
                        Size = new FileInfo(gmaFile).Length,
                        LastUpdated = File.GetLastWriteTime(gmaFile),
                        IsGmaFile = true,
                        IsEnabled = true,
                        IsAvailable = true,
                        IsDownloadPending = false,
                        FirstSeenSubscribedAtUtc =
                            config.SubscriptionFirstSeenAtUtc.TryGetValue(addonId, out var firstSeen)
                                ? firstSeen
                                : (DateTime?)null
                    };
                    
                    // Try to read metadata
                    ReadGmaMetadata(gmaFile, addon);
                    
                    newAddons.Add(addon);
                }
            }
            
            PendingDownloadCount = subscriptionTruthAvailable
                ? cachedAddonIds.Except(downloadedAddonIds, StringComparer.Ordinal).Count()
                : 0;
            if (observation.Changed)
            {
                await SaveConfigurationAsync();
            }

            await ApplyPendingNewSubscriptionStatesAsync(
                workshopSnapshot,
                observation,
                "ScanForNewAddons");

            return newAddons;
        }

        public Task<WorkshopAddon?> ScanAddonAsync(string addonPath)
        {
            return ScanAddonAsyncCore(addonPath, payloadAlreadyValidated: false);
        }

        private async Task<WorkshopAddon?> ScanAddonAsyncCore(
            string addonPath,
            bool payloadAlreadyValidated)
        {
            string addonId = Path.GetFileName(addonPath);
            
            if (!long.TryParse(addonId, out _))
            {
                return null;
            }

            if (!payloadAlreadyValidated &&
                !DirectoryHasAddonPayload(addonPath, "ScanAddonAsync"))
            {
                return null;
            }

            var addon = new WorkshopAddon(addonId, addonPath);
            
            string junctionPath = Path.Combine(workshopPath, addonId);
            addon.IsEnabled = Directory.Exists(junctionPath) && junctionService.IsJunction(junctionPath);

            string gmaPath = Path.Combine(addonPath, "*.gma");
            var gmaFiles = await Task.Run(() => Directory.GetFiles(addonPath, "*.gma"));
            
            if (gmaFiles.Length > 0)
            {
                ReadGmaMetadata(gmaFiles[0], addon);
            }
            else
            {
                // GMAファイルがない場合はSteam APIからタイトルを取得してみる
                // No GMA file found - will try Steam API
            }
            
            // タイトルが空の場合はWorkshop IDを使用
            if (string.IsNullOrWhiteSpace(addon.Title))
            {
                addon.Title = $"Workshop-{addonId}";
            }

            var dirInfo = new DirectoryInfo(addonPath);
            addon.Size = await CalculateDirectorySizeAsync(dirInfo);
            addon.LastUpdated = dirInfo.LastWriteTimeUtc;

            return addon;
        }

        private void ReadGmaMetadata(string gmaPath, WorkshopAddon addon)
        {
            try
            {
                using (var stream = new FileStream(gmaPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8))
                {
                    // ファイルが最小限のサイズを持っているか確認
                    if (stream.Length < 22) // GMAD(4) + version(1) + steamId(8) + timestamp(8) + requiredContentCount(1) = 22 bytes minimum
                    {
                        errorHandler.HandleWarning($"GMA file {gmaPath} is too small to be valid", "ReadGmaMetadata");
                        return;
                    }

                    // マジックナンバーをバイト配列として読み取り
                    var magicBytes = reader.ReadBytes(4);
                    var magic = System.Text.Encoding.ASCII.GetString(magicBytes);
                    if (!magic.Equals("GMAD", StringComparison.Ordinal))
                    {
                        return;
                    }

                    byte version = reader.ReadByte();
                    
                    ulong steamId = reader.ReadUInt64();
                    ulong timestamp = reader.ReadUInt64();
                    
                    // タイムスタンプをDateTimeに変換
                    if (timestamp > 0)
                    {
                        addon.LastUpdated = DateTimeOffset.FromUnixTimeSeconds((long)timestamp).DateTime;
                    }
                    
                    // Required content の個数を読み取り、各要素をスキップ
                    byte requiredContentCount = reader.ReadByte();
                    for (int i = 0; i < requiredContentCount; i++)
                    {
                        ReadNullTerminatedString(reader); // 読み捨て
                    }

                    string name = ReadNullTerminatedString(reader);
                    // タイトルが短すぎる場合や特定のプレフィックスの場合は無効とみなす
                    if (!string.IsNullOrEmpty(name) && !name.StartsWith("tag", StringComparison.OrdinalIgnoreCase))
                    {
                        addon.Title = name;
                    }

                    string description = ReadNullTerminatedString(reader);
                    if (!string.IsNullOrEmpty(description))
                    {
                        addon.Description = description;
                    }
                    
                    string author = ReadNullTerminatedString(reader);
                    if (!string.IsNullOrEmpty(author))
                    {
                        addon.Author = author;
                    }
                }
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning($"Failed to read GMA metadata from {gmaPath}: {ex.Message}", "ReadGmaMetadata");
                string gmaName = Path.GetFileNameWithoutExtension(gmaPath);
                addon.Title = gmaName;
            }
            
            // タイトルが空の場合はGMAファイル名を使用
            if (string.IsNullOrWhiteSpace(addon.Title))
            {
                addon.Title = Path.GetFileNameWithoutExtension(gmaPath);
            }
        }

        private bool ApplyWorkshopCacheTags(IEnumerable<WorkshopAddon> addons)
        {
            if (addons == null)
            {
                return false;
            }

            Dictionary<string, WorkshopItemInfo> cachedDetails;
            try
            {
                cachedDetails = GetAddonDetailsFromCache();
            }
            catch
            {
                return false;
            }

            if (cachedDetails == null || cachedDetails.Count == 0)
            {
                return false;
            }

            var updated = false;
            foreach (var addon in addons)
            {
                if (addon == null || addon.IsLocal)
                {
                    continue;
                }

                if (!cachedDetails.TryGetValue(addon.Id, out var info))
                {
                    continue;
                }

                if (info.TimeUpdated.HasValue)
                {
                    var workshopUpdatedAtUtc =
                        NormalizeWorkshopTimestampUtc(info.TimeUpdated.Value);
                    if (addon.WorkshopUpdatedAtUtc != workshopUpdatedAtUtc)
                    {
                        addon.WorkshopUpdatedAtUtc = workshopUpdatedAtUtc;
                        updated = true;
                    }
                }

                if ((addon.Tags != null && addon.Tags.Length > 0) ||
                    string.IsNullOrWhiteSpace(info.Tags))
                {
                    continue;
                }

                var tags = SplitTagsFromCache(info.Tags);
                if (tags.Length > 0)
                {
                    addon.Tags = tags;
                    updated = true;
                }
            }

            return updated;
        }

        private static DateTime NormalizeWorkshopTimestampUtc(DateTime value)
        {
            return value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
            };
        }

        private static string[] SplitTagsFromCache(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Array.Empty<string>();
            }

            var separators = (raw.Contains(',') || raw.Contains(';'))
                ? new[] { ',', ';' }
                : new[] { ' ', '\t', '\r', '\n' };

            return raw
                .Split(separators, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .ToArray();
        }

        private string ReadNullTerminatedString(BinaryReader reader)
        {
            var bytes = new List<byte>();
            try
            {
                byte b;
                while ((b = reader.ReadByte()) != 0)
                {
                    bytes.Add(b);
                    // 安全のため、文字列が長すぎる場合は中断
                    if (bytes.Count > 1024)
                    {
                        break;
                    }
                }
            }
            catch (EndOfStreamException)
            {
                // ストリームの終端に達した場合は、これまでに読み取ったバイトを返す
                // GMAファイルが不完全な場合やフォーマットが異なる場合に発生する可能性がある
                }
            return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
        }
        
        // GMAファイルからタイトルのみを高速に読み取る専用メソッド
        private async Task<string?> ReadGmaTitleOnlyAsync(string gmaPath)
        {
            await Task.CompletedTask;
            try
            {
                // FileShare.Readで他のプロセスとの競合を回避
                using (var fs = new FileStream(gmaPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var br = new BinaryReader(fs, System.Text.Encoding.UTF8))
                {
                    // ファイルが最小限のサイズを持っているか確認
                    if (fs.Length < 22) // GMAD(4) + version(1) + steamId(8) + timestamp(8) + requiredContentCount(1) = 22 bytes minimum
                    {
                        return null;
                    }

                    // タイトルが特定のプレフィックスの場合は無効とみなす
                    var magicBytes = br.ReadBytes(4);
                    var magic = System.Text.Encoding.ASCII.GetString(magicBytes);
                    if (!magic.Equals("GMAD", StringComparison.Ordinal))
                        return null;
                    
                    var version = br.ReadByte();
                    br.ReadUInt64(); // SteamID
                    br.ReadUInt64(); // Timestamp
                    var requiredContentCount = br.ReadByte();
                    
                    // Skip required content if exists
                    for (int i = 0; i < requiredContentCount; i++)
                    {
                        ReadNullTerminatedString(br); // 読み捨て
                    }
                    
        /// <summary>
                    var bytes = new List<byte>();
                    try
                    {
                        byte b;
                        while ((b = br.ReadByte()) != 0)
                        {
                            bytes.Add(b);
                            if (bytes.Count > 1024) break;
                        }
                    }
                    catch (EndOfStreamException)
                    {
                        // ストリームの終端に達した場合は、これまでに読み取ったバイトを返す
                    }
                    
                    var title = System.Text.Encoding.UTF8.GetString(bytes.ToArray());
                    
                    // タイトルが特定のプレフィックスの場合は無効とみなす
                    if (!string.IsNullOrWhiteSpace(title) && !title.StartsWith("tag", StringComparison.OrdinalIgnoreCase))
                    {
                        return title;
                    }
                    
                    return null;
                }
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning($"Failed to read GMA title from {gmaPath}: {ex.Message}", "ReadGmaTitleOnly");
                return null;
            }
        }
        
        // キャッシュアドオンの名前のみを高速に取得する新メソッド
        /// <summary>
        /// バックグラウンドでアドオンのタイトルを更新する
        /// </summary>
        public async Task UpdateAddonTitlesInBackgroundAsync()
        {
            await Task.Run(async () =>
            {
                var addonsToUpdate = configuration.AddonMetadata
                    .Where(kvp => !kvp.Value.IsLocal &&
                           (kvp.Value.NeedsTitleUpdate || 
                            (kvp.Value.IsGmaFile && kvp.Value.Title.StartsWith("Workshop-"))))
                    .ToList();
                var updated = false;

                foreach (var kvp in addonsToUpdate)
                {
                    try
                    {
                        if (kvp.Value.IsGmaFile)
                        {
                            var gmaPath = Path.Combine(gmodCachePath, kvp.Key, $"{kvp.Key}.gma");
                            if (File.Exists(gmaPath))
                            {
                                var title = await ReadGmaTitleOnlyAsync(gmaPath);
                                if (!string.IsNullOrWhiteSpace(title) && !title.StartsWith("Workshop-"))
                                {
                                    kvp.Value.Title = title;
                                    kvp.Value.NeedsTitleUpdate = false;
                                    
            // 並列処理でタイトルを読み取る
                                    updated = true;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // エラーは無視して続行
                        }
                    
                    // 少し待機して負荷を分散
                    await Task.Delay(50);
                }

                if (updated)
                {
                    await SaveConfigurationAsync();
                }
            });
        }
        
	        public async Task UpdateCacheAddonTitlesAsync(IProgress<(int current, int total, string message)>? progress = null)
	        {
            var cacheAddons = configuration.AddonMetadata
                .Where(kvp => !kvp.Value.IsLocal &&
                       kvp.Value.IsGmaFile && 
                       (kvp.Value.Title == kvp.Key || kvp.Value.Title.StartsWith("Workshop-")))
                .ToList();
            
            if (cacheAddons.Count == 0)
                return;
            
            int processed = 0;
            int total = cacheAddons.Count;
            
            // 並列処理でタイトルを読み取る
            var titleTasks = cacheAddons.Select(async kvp =>
            {
                var addonId = kvp.Key;
                var addon = kvp.Value;
                
                // GMAファイルのパスを構築
                string? gmaPath = null;
                
                // まずキャッシュマネージャーパスを確認
                if (!string.IsNullOrEmpty(gmodCacheAddonsPath))
                {
                    gmaPath = Path.Combine(gmodCacheAddonsPath, $"{addonId}.gma");
                    if (!File.Exists(gmaPath))
                        gmaPath = null;
                }
                
                // 次にキャッシュパスを確認
                if (gmaPath == null && !string.IsNullOrEmpty(gmodCachePath))
                {
                    gmaPath = Path.Combine(gmodCachePath, $"{addonId}.gma");
                    if (!File.Exists(gmaPath))
                        gmaPath = null;
                }
                
                if (gmaPath != null && File.Exists(gmaPath))
                {
                    // タイトルのみを高速に読み取る
                    string? title = await ReadGmaTitleOnlyAsync(gmaPath);
                    
                    if (!string.IsNullOrEmpty(title) && title != addonId && 
                        title.Length > 3 && !title.Equals("tag", StringComparison.OrdinalIgnoreCase))
                    {
                        return (addonId, title);
                    }
                }
                
                return (addonId, (string?)null);
            });
            
            var results = await Task.WhenAll(titleTasks);
            
            // 結果を適用してプログレスを更新
            foreach (var (addonId, title) in results)
            {
                if (!string.IsNullOrEmpty(title))
                {
                    var addon = configuration.AddonMetadata[addonId];
                    addon.Title = title;
                    addon.NeedsTitleUpdate = false; // タイトル更新完了フラグをクリア
                }
                
                processed++;
                var currentAddon = configuration.AddonMetadata[addonId];
                progress?.Report((processed, total, $"Processing: {currentAddon.Title}"));
            }
            
            // 設定を保存
            await SaveConfigurationAsync();
        }

        private async Task<long> CalculateDirectorySizeAsync(DirectoryInfo directory)
        {
            return await Task.Run(() =>
            {
                try
                {
                    return directory.GetFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
                }
                catch
                {
                    return 0;
                }
            });
        }

        private static bool HasNonEmptyFile(string path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path) &&
                       File.Exists(path) &&
                       new FileInfo(path).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private HashSet<string>? CaptureDisabledAddonIdsForScan()
        {
            if (gmodAddonStateStore == null)
            {
                return null;
            }

            try
            {
                var snapshot = gmodAddonStateStore.ReadSnapshot();
                return snapshot.IsValidFormat
                    ? new HashSet<string>(snapshot.DisabledIds, StringComparer.Ordinal)
                    : null;
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning(
                    $"Failed to read addonnomount.txt once for Workshop scan: {ex.Message}",
                    "ScanWorkshopFolderSoftAsync");
                return null;
            }
        }

        private bool DirectoryHasAddonPayload(
            string directoryPath,
            string operationName,
            bool logInvalidPayload = true)
        {
            var result = AddonPayloadValidator.Validate(directoryPath);
            if (result.IsValid)
            {
                return true;
            }

            if (logInvalidPayload &&
                !string.IsNullOrWhiteSpace(directoryPath) &&
                Directory.Exists(directoryPath))
            {
                errorHandler.HandleInfo(
                    $"Skipping invalid addon payload at {directoryPath}: {string.Join("; ", result.Reasons)}",
                    operationName);
            }

            return false;
        }

        private void ValidatePath(string? path, string paramName)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Path cannot be null or empty.", paramName);
            }

            // Check for invalid characters
            char[] invalidChars = Path.GetInvalidPathChars();
            if (path.IndexOfAny(invalidChars) >= 0)
            {
                throw new ArgumentException($"Path contains invalid characters: {path}", paramName);
            }

            // Prevent path traversal attacks - enhanced check
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Invalid path format: {path}", paramName, ex);
            }
            
            // Check for any path traversal attempts
            if (path.Contains("..", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Path traversal detected in: {path}", paramName);
            }
            
            // Check for Windows special paths
            if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) ||
                (path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) && path.Length > 1 && path[1] == '\\'))
            {
                throw new ArgumentException($"Special path format not allowed: {path}", paramName);
            }

            // Ensure the path is within expected boundaries
            if (!string.IsNullOrEmpty(workshopPath) && !string.IsNullOrEmpty(managerPath))
            {
                bool isInWorkshop = fullPath.StartsWith(workshopPath, StringComparison.OrdinalIgnoreCase);
                bool isInManager = fullPath.StartsWith(managerPath, StringComparison.OrdinalIgnoreCase);
                bool isInGmodCache = !string.IsNullOrEmpty(gmodCachePath) && fullPath.StartsWith(gmodCachePath, StringComparison.OrdinalIgnoreCase);
                bool isInAppDirectory = fullPath.StartsWith(AppDomain.CurrentDomain.BaseDirectory, StringComparison.OrdinalIgnoreCase);
                bool isInGmodRoot = !string.IsNullOrEmpty(gmodRootPath) && fullPath.StartsWith(gmodRootPath, StringComparison.OrdinalIgnoreCase);
                
                if (!isInWorkshop && !isInManager && !isInGmodCache && !isInAppDirectory && !isInGmodRoot)
                {
                    throw new ArgumentException($"Path is outside allowed directories: {path}", paramName);
                }
            }
        }

        private static bool GetStrictLinkModeFromEnvironment()
        {
            var value = Environment.GetEnvironmentVariable("GAM_STRICT_LINK_MODE");
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

	        public void EnableAddon(string addonId)
	        {
	            if (TryHandleLocalAddonToggle(addonId, enable: true))
	            {
	                return;
	            }
	            if (!IsBulkStateUpdate)
	            {
	                InvalidateWorkshopScanCache();
	            }
	            if (IsBulkStateUpdate)
	            {
	                modeStrategy.EnableAddon(this, addonId);
	                return;
	            }

	            _runtimeReconcileGate.Wait();
	            try
	            {
	                modeStrategy.EnableAddon(this, addonId);
	            }
	            finally
	            {
	                _runtimeReconcileGate.Release();
	            }
	        }

	        internal void EnableAddonHard(string addonId)
	        {
            // Exception: if we removed a disabled stub, fall through to restore workshop/cache presence.
            if (!IsBulkStateUpdate)
            {
                if (!TryPersistSingleAddonRuntimeState(addonId, true, "EnableAddon"))
                {
                    throw new InvalidOperationException(
                        $"Failed to persist enabled state for addon {addonId}.");
                }
            }

            // Remove any legacy stub directory first (for backward compatibility)
            bool removedStub = RemoveDisabledStub(workshopPath, addonId);
            TryRestoreMovedAsideWorkshopFolder(addonId);
            
            // Check if this is a GMA file addon - both from metadata and runtime check
            var addonInfo = configuration.AddonMetadata.ContainsKey(addonId) ? configuration.AddonMetadata[addonId] : null;
            
            // Runtime check for GMA files in cache
            bool isGmaRuntime = IsGmaAddonRuntime(addonId);

            // Soft mode: only toggle addonnomount.txt / metadata (no filesystem operations)
            // Exception: if we removed a disabled stub, fall through to restore workshop/cache presence.
            if (DisableMode == DisableMode.Soft && !removedStub)
            {
                if (Interlocked.Exchange(ref _softModeNoFileOpsNoticeLogged, 1) == 0)
                {
                    errorHandler.HandleInfo(
                        "DisableMode=Soft: ON/OFF will only update garrysmod/cfg/addonnomount.txt (no workshop/cache file operations).",
                        "EnableAddon");
                }
                if (addonInfo != null)
                {
                    addonInfo.IsEnabled = true;
                    if (isGmaRuntime && !addonInfo.IsGmaFile)
                    {
                        addonInfo.IsGmaFile = true;
                    }
                }
                return;
            }
            
	            if (isGmaRuntime || (addonInfo != null && addonInfo.IsGmaFile))
	            {
                // GMAファイルがない場合は従来のジャンクション方式を使用
	                if (addonInfo != null && !addonInfo.IsGmaFile && isGmaRuntime)
	                {
	                    errorHandler.HandleWarning($"Addon {addonId} detected as GMA at runtime but metadata says otherwise. Updating metadata.", "EnableAddon");
	                    addonInfo.IsGmaFile = true;
	                }
	                
	                EnableGmaAddon(addonId);
	                if (addonInfo != null)
	                {
	                    addonInfo.IsEnabled = true;
	                }
	                return;
	            }

            string sourcePath = Path.Combine(addonsPath, addonId);
            string workshopAddonPath = Path.Combine(workshopPath, addonId);
            ValidatePath(sourcePath, "sourcePath");
            ValidatePath(workshopAddonPath, "workshopAddonPath");

            if (!Directory.Exists(sourcePath))
            {
                throw new DirectoryNotFoundException($"Addon not found: {addonId}");
            }

            // 新方式: 通常のディレクトリを作成し、中のGMAファイルだけハードリンク化
            string sourceGmaPath = Path.Combine(sourcePath, $"{addonId}.gma");
            
	            if (File.Exists(sourceGmaPath))
	            {
	                        // ジャンクションが既に存在する場合も、ターゲット整合性のためCreateJunctionに処理を委ねる
	                junctionService.CreateWorkshopAddonStructure(workshopPath, addonId, sourceGmaPath);
	            }
	            else
	            {
                // GMAファイルがない場合は従来のジャンクション方式を使用
                if (Directory.Exists(workshopAddonPath))
                {
                    if (!junctionService.IsJunction(workshopAddonPath))
                    {
                        // 実体フォルダが存在する場合、まず管理フォルダに移動
                        errorHandler.HandleWarning($"Found real folder instead of junction for addon {addonId}. Converting to managed addon.", "EnableAddon");
                        
                        string tempPath = workshopAddonPath + "_temp_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                        ValidatePath(tempPath, "tempPath");
                        Directory.Move(workshopAddonPath, tempPath);
                        
                        try
                        {
                            // 既存の管理フォルダとマージ
                            MergeDirectories(tempPath, sourcePath);
                            Directory.Delete(tempPath, true);
                        }
                        catch
                        {
                            // 失敗した場合は元に戻す
                            if (Directory.Exists(tempPath))
                            {
                                Directory.Move(tempPath, workshopAddonPath);
                            }
                            throw;
                        }
                    }
	                    else
	                    {
	                        // ジャンクションが既に存在する場合も、ターゲット整合性のためCreateJunctionに処理を委ねる
	                        }
	                }

	                CreateJunctionWithMetrics(workshopAddonPath, sourcePath);
	            }

            if (addonInfo != null)
            {
                addonInfo.IsEnabled = true;
                if (File.Exists(sourceGmaPath))
                {
                    addonInfo.IsGmaFile = true;
                }
            }
        }

        internal void EnableAddonSoft(string addonId)
        {
            if (!IsBulkStateUpdate)
            {
                if (!TryPersistSingleAddonRuntimeState(addonId, true, "EnableAddon"))
                {
                    throw new InvalidOperationException(
                        $"Failed to persist enabled state for addon {addonId}.");
                }
            }

            if (Interlocked.Exchange(ref _softModeNoFileOpsNoticeLogged, 1) == 0)
            {
                errorHandler.HandleInfo(
                    "DisableMode=Soft: ON/OFF will only update garrysmod/cfg/addonnomount.txt (no workshop/cache file operations).",
                    "EnableAddon");
            }

            var addonInfo = configuration.AddonMetadata.ContainsKey(addonId) ? configuration.AddonMetadata[addonId] : null;
            var isGmaRuntime = IsGmaAddonRuntime(addonId);
            if (addonInfo != null)
            {
                addonInfo.IsEnabled = true;
                if (isGmaRuntime)
                {
                    addonInfo.IsGmaFile = true;
                }
            }
        }

        public void DisableAddon(string addonId)
        {
            if (TryHandleLocalAddonToggle(addonId, enable: false))
            {
                return;
            }
            if (!IsBulkStateUpdate)
            {
                InvalidateWorkshopScanCache();
            }
            if (IsBulkStateUpdate)
            {
                modeStrategy.DisableAddon(this, addonId);
                return;
            }

            _runtimeReconcileGate.Wait();
            try
            {
                modeStrategy.DisableAddon(this, addonId);
            }
            finally
            {
                _runtimeReconcileGate.Release();
            }
        }

        internal void DisableAddonHard(string addonId)
        {
            if (!IsBulkStateUpdate)
            {
                if (!TryPersistSingleAddonRuntimeState(addonId, false, "DisableAddon"))
                {
                    throw new InvalidOperationException(
                        $"Failed to persist disabled state for addon {addonId}.");
                }
            }

	            var runtimeIsGma = IsGmaAddonRuntime(addonId);
	            var addonInfo = configuration.AddonMetadata.ContainsKey(addonId) ? configuration.AddonMetadata[addonId] : null;
	            var isGmaAddon = runtimeIsGma || (addonInfo?.IsGmaFile ?? false);
	
	            if (addonInfo != null)
	            {
	                addonInfo.IsEnabled = false;
	                if (runtimeIsGma && !addonInfo.IsGmaFile)
	                {
	                    addonInfo.IsGmaFile = true;
	                    isGmaAddon = true;
	                }
	            }

	            if (DisableMode == DisableMode.Soft)
	            {
	                // ソフト無効化: ファイル構造は残し、addonnomount.txtとメタデータのみ更新
	                return;
	            }
	
	            // ハード無効化: 先にGMAの管理コピーを確保してから削除/移動する（戻らない問題の防止）
	            if (isGmaAddon)
	            {
	                try
	                {
	                    var managedGma = EnsureManagedGmaAvailable(addonId, addonInfo);
	                    if (managedGma == null)
	                    {
	                        errorHandler.HandleWarning(
	                            $"Hard disable skipped for addon {addonId} because no GMA source could be located; performed soft disable only. {BuildGmaSourceDiagnostics(addonId, addonInfo)}",
	                            "DisableAddon");
	                        return;
	                    }
	                }
	                catch (Exception ex)
	                {
	                    errorHandler.HandleWarning($"Failed to ensure managed GMA copy for addon {addonId}: {ex.Message}", "DisableAddon");
	                    return;
	                }
	            }
	

            // Remove legacy stub directories if they exist
            RemoveDisabledStub(workshopPath, addonId);

	            // Tear down workshop presence to keep state consistent while disabled
	            RemoveWorkshopPresence(addonId, isGmaAddon);

            // Leave a lightweight stub to discourage immediate Steam re-download
            CreateDisabledStub(workshopPath, addonId);
        }

        internal void DisableAddonSoft(string addonId)
        {
            if (!IsBulkStateUpdate)
            {
                if (!TryPersistSingleAddonRuntimeState(addonId, false, "DisableAddon"))
                {
                    throw new InvalidOperationException(
                        $"Failed to persist disabled state for addon {addonId}.");
                }
            }

            var runtimeIsGma = IsGmaAddonRuntime(addonId);
            var addonInfo = configuration.AddonMetadata.ContainsKey(addonId) ? configuration.AddonMetadata[addonId] : null;
            if (addonInfo != null)
            {
                addonInfo.IsEnabled = false;
                if (runtimeIsGma)
                {
                    addonInfo.IsGmaFile = true;
                }
            }
        }

	        private void EnableGmaAddon(string addonId)
	        {
            if (!IsBulkStateUpdate)
            {
                if (!TryPersistSingleAddonRuntimeState(addonId, true, "EnableGmaAddon"))
                {
                    throw new InvalidOperationException(
                        $"Failed to persist enabled state for GMA addon {addonId}.");
                }
            }
            
		            // Remove any legacy stub directory first (for backward compatibility)
		            RemoveDisabledStub(workshopPath, addonId);
		            TryRestoreMovedAsideWorkshopFolder(addonId);

		            var addonInfo = configuration.AddonMetadata.ContainsKey(addonId) ? configuration.AddonMetadata[addonId] : null;

			            // Soft mode: do not touch workshop/cache files; addonnomount.txt is enough.
			            if (DisableMode == DisableMode.Soft)
			            {
			                if (Interlocked.Exchange(ref _softModeNoFileOpsNoticeLogged, 1) == 0)
			                {
			                    errorHandler.HandleInfo(
			                        "DisableMode=Soft: ON/OFF will only update garrysmod/cfg/addonnomount.txt (no workshop/cache file operations).",
			                        "EnableGmaAddon");
			                }
			                if (addonInfo != null)
			                {
			                    addonInfo.IsEnabled = true;
			                    addonInfo.IsGmaFile = true;
			                }
		                return;
		            }
		
		            var sourceGmaPath = ResolveGmaSourcePath(addonId, addonInfo);
	            if (sourceGmaPath == null)
	            {
	                errorHandler.HandleWarning(
	                    $"GMA file for addon {addonId} could not be located; skipping enable operation. {BuildGmaSourceDiagnostics(addonId, addonInfo)}",
	                    "EnableGmaAddon");
	                return;
	            }
	
	            var managedGmaPath = EnsureManagedGmaAvailable(addonId, addonInfo, sourceGmaPath);
	            var primaryGmaPath = managedGmaPath ?? sourceGmaPath;
	
	            EnsureWorkshopStructureForGma(addonId, primaryGmaPath);
	            EnsureCacheStructureForGma(addonId, primaryGmaPath);

		            if (addonInfo != null)
		            {
		                addonInfo.IsEnabled = true;
		                addonInfo.IsGmaFile = true;
		            }
		        }

        private void EnsureWorkshopContentPresence(string addonId)
        {
            var addonInfo = configuration.AddonMetadata.ContainsKey(addonId) ? configuration.AddonMetadata[addonId] : null;
            bool isGmaAddon = addonInfo?.IsGmaFile ?? IsGmaAddonRuntime(addonId);

            if (isGmaAddon)
            {
                var sourcePath = ResolveGmaSourcePath(addonId, addonInfo);
                if (sourcePath != null)
                {
                    try
                    {
                        EnsureWorkshopStructureForGma(addonId, sourcePath);
                    }
                    catch (Exception ex)
                    {
                        errorHandler.HandleWarning($"Failed to ensure workshop structure for addon {addonId}: {ex.Message}", "EnsureWorkshopContentPresence");
                    }
                }
                return;
            }

            string managedPath = Path.Combine(addonsPath, addonId);
            ValidatePath(managedPath, "managedAddonPath");
            if (!Directory.Exists(managedPath))
            {
                return;
            }

            string workshopAddonPath = Path.Combine(workshopPath, addonId);
            ValidatePath(workshopAddonPath, "workshopAddonPath");

            if (!Directory.Exists(workshopAddonPath) || !junctionService.IsJunction(workshopAddonPath))
            {
                try
                {
                    CreateJunctionWithMetrics(workshopAddonPath, managedPath);
                }
                catch (Exception ex)
                {
                    errorHandler.HandleWarning($"Failed to ensure workshop junction for addon {addonId}: {ex.Message}", "EnsureWorkshopContentPresence");
                }
            }
        }

		        private string? ResolveGmaSourcePath(string addonId, WorkshopAddon? addonInfo)
		        {
		            var candidates = new List<string>();

		            if (addonInfo != null &&
		                !string.IsNullOrEmpty(addonInfo.FolderPath) &&
		                addonInfo.FolderPath.EndsWith(".gma", StringComparison.OrdinalIgnoreCase))
		            {
		                candidates.Add(addonInfo.FolderPath);
		            }

		            // Workshopの生データ（信頼できるソース）
		            candidates.Add(Path.Combine(workshopPath, addonId, $"{addonId}.gma"));
		            candidates.Add(Path.Combine(workshopPath, addonId, $"{addonId}.cache"));

		            // 管理フォルダ（優先）
		            if (!string.IsNullOrEmpty(gmodCacheAddonsPath))
		            {
		                candidates.Add(Path.Combine(gmodCacheAddonsPath, addonId + ".gma"));
		            }

		            candidates.Add(Path.Combine(addonsPath, addonId, $"{addonId}.gma"));
		            candidates.Add(Path.Combine(addonsPath, $"{addonId}.gma"));

		            // GModキャッシュ（最後の手段）
		            if (!string.IsNullOrEmpty(gmodCachePath))
		            {
		                candidates.Add(Path.Combine(gmodCachePath, addonId + ".gma"));
		                candidates.Add(Path.Combine(gmodCachePath, addonId + ".cache"));
		            }

		            string? best = null;
		            long bestLength = -1;

		            foreach (var candidate in candidates)
		            {
		                try
		                {
		                    ValidatePath(candidate, "gmaCandidatePath");
		                    if (!File.Exists(candidate))
		                    {
		                        continue;
		                    }

		                    if (!LooksLikeGmaFile(candidate))
		                    {
		                        continue;
		                    }

		                    var length = new FileInfo(candidate).Length;
		                    if (length > bestLength)
		                    {
		                        best = candidate;
		                        bestLength = length;
		                    }
		                }
		                catch
		                {
		                    // Ignore invalid candidate paths
		                }
		            }

		            return best;
		        }

                private string BuildGmaSourceDiagnostics(string addonId, WorkshopAddon? addonInfo)
                {
                    try
                    {
                        var candidates = new List<string>();

                        if (addonInfo != null &&
                            !string.IsNullOrEmpty(addonInfo.FolderPath) &&
                            addonInfo.FolderPath.EndsWith(".gma", StringComparison.OrdinalIgnoreCase))
                        {
                            candidates.Add(addonInfo.FolderPath);
                        }

                        candidates.Add(Path.Combine(workshopPath, addonId, $"{addonId}.gma"));
                        candidates.Add(Path.Combine(workshopPath, addonId, $"{addonId}.cache"));

                        if (!string.IsNullOrEmpty(gmodCacheAddonsPath))
                        {
                            candidates.Add(Path.Combine(gmodCacheAddonsPath, addonId + ".gma"));
                        }

                        candidates.Add(Path.Combine(addonsPath, addonId, $"{addonId}.gma"));
                        candidates.Add(Path.Combine(addonsPath, $"{addonId}.gma"));

                        if (!string.IsNullOrEmpty(gmodCachePath))
                        {
                            candidates.Add(Path.Combine(gmodCachePath, addonId + ".gma"));
                            candidates.Add(Path.Combine(gmodCachePath, addonId + ".cache"));
                        }

                        var unique = candidates
                            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        var rows = new List<string>();
                        var headerBuffer = new byte[4];
                        foreach (var candidate in unique.Take(10))
                        {
                            try
                            {
                                ValidatePath(candidate, "gmaCandidatePath");
                                if (!File.Exists(candidate))
                                {
                                    rows.Add($"{candidate}: missing");
                                    continue;
                                }

                                long length;
                                try
                                {
                                    length = new FileInfo(candidate).Length;
                                }
                                catch
                                {
                                    length = -1;
                                }

                                string header = "????";
                                try
                                {
                                    using var stream = File.Open(candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                                    int read = stream.Read(headerBuffer, 0, headerBuffer.Length);
                                    if (read == headerBuffer.Length)
                                    {
                                        header = $"{(char)headerBuffer[0]}{(char)headerBuffer[1]}{(char)headerBuffer[2]}{(char)headerBuffer[3]}";
                                    }
                                }
                                catch
                                {
                                    header = "readfail";
                                }

                                bool looksLikeGma = length >= 8 && header == "GMAD";
                                rows.Add($"{candidate}: len={length} header={header} gma={looksLikeGma}");
                            }
                            catch (Exception ex)
                            {
                                rows.Add($"{candidate}: invalid ({ex.GetType().Name})");
                            }
                        }

                        return "GMA candidates: " + string.Join(" | ", rows);
                    }
                    catch (Exception ex)
                    {
                        return $"GMA candidates: (failed to inspect: {ex.GetType().Name})";
                    }
                }

		        private bool LooksLikeGmaFile(string path)
		        {
		            try
		            {
		                ValidatePath(path, "gmaPath");
		                if (!File.Exists(path))
		                {
		                    return false;
		                }

		                var fileInfo = new FileInfo(path);
		                if (fileInfo.Length < 8)
		                {
		                    return false;
		                }

		                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		                Span<byte> header = stackalloc byte[4];
		                var read = stream.Read(header);
		                if (read != 4)
		                {
		                    return false;
		                }

		                return header[0] == (byte)'G' &&
		                       header[1] == (byte)'M' &&
		                       header[2] == (byte)'A' &&
		                       header[3] == (byte)'D';
		            }
		            catch
		            {
		                return false;
		            }
		        }
		
		        private string? EnsureManagedGmaAvailable(string addonId, WorkshopAddon? addonInfo, string? preferredSourcePath = null)
		        {
		            try
		            {
		                string managedGmaPath = Path.Combine(addonsPath, addonId, $"{addonId}.gma");
		
		                ValidatePath(managedGmaPath, "managedGmaPath");

		                long? managedLength = null;
		                if (File.Exists(managedGmaPath))
		                {
		                    if (LooksLikeGmaFile(managedGmaPath))
		                    {
		                        managedLength = new FileInfo(managedGmaPath).Length;
		                    }
		                    else
		                    {
		                        try
		                        {
		                            File.SetAttributes(managedGmaPath, FileAttributes.Normal);
		                        }
		                        catch (Exception ex)
		                        {
		                            errorHandler.HandleWarning(
		                                $"Failed to normalize attributes for invalid managed GMA {managedGmaPath}: {ex.Message}",
		                                "EnsureManagedGmaAvailable");
		                        }

		                        try
		                        {
		                            File.Delete(managedGmaPath);
		                        }
		                        catch (Exception ex)
		                        {
		                            errorHandler.HandleWarning(
		                                $"Failed to delete invalid managed GMA {managedGmaPath}: {ex.Message}",
		                                "EnsureManagedGmaAvailable");
		                        }
		                    }
		                }
		
		                string? source = null;
		                if (!string.IsNullOrEmpty(preferredSourcePath))
		                {
		                    try
		                    {
		                        ValidatePath(preferredSourcePath, "preferredSourcePath");
		                        if (LooksLikeGmaFile(preferredSourcePath))
		                        {
		                            source = preferredSourcePath;
		                        }
		                    }
		                    catch
		                    {
		                        // Ignore invalid preferred path
		                    }
		                }
		
		                source ??= ResolveGmaSourcePath(addonId, addonInfo);

		                // If we already have a valid managed copy and can't locate a better source, keep it.
		                if (string.IsNullOrEmpty(source) || !LooksLikeGmaFile(source))
		                {
		                    return managedLength.HasValue ? managedGmaPath : null;
		                }
		
		                if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(managedGmaPath), StringComparison.OrdinalIgnoreCase))
		                {
		                    return managedGmaPath;
		                }

		                var sourceLength = new FileInfo(source).Length;
		                if (managedLength.HasValue && managedLength.Value >= sourceLength)
		                {
		                    return managedGmaPath;
		                }
		
		                var managedDirectory = Path.GetDirectoryName(managedGmaPath);
		                if (!string.IsNullOrEmpty(managedDirectory) && !Directory.Exists(managedDirectory))
		                {
		                    Directory.CreateDirectory(managedDirectory);
		                }

		                if (File.Exists(managedGmaPath))
		                {
		                    try
		                    {
		                        File.SetAttributes(managedGmaPath, FileAttributes.Normal);
		                    }
		                    catch (Exception ex)
		                    {
		                        errorHandler.HandleWarning(
		                            $"Failed to normalize attributes for managed GMA {managedGmaPath}: {ex.Message}",
		                            "EnsureManagedGmaAvailable");
		                    }

		                    File.Delete(managedGmaPath);
		                }
		
                        if (AreSameDrive(source, managedGmaPath))
                        {
                            if (!CreateHardLinkSafe(managedGmaPath, source))
                            {
                                CopyFileForLinkFallback(addonId, source, managedGmaPath, "EnsureManagedGmaAvailable");
                            }
                        }
                        else
                        {
                            CopyFileForLinkFallback(addonId, source, managedGmaPath, "EnsureManagedGmaAvailable");
                        }

                        return managedGmaPath;
                    }
                    catch (Exception ex)
                    {
                        if (ex is StrictLinkModeException)
                        {
                            throw;
                        }

                        errorHandler.HandleWarning($"Failed to ensure managed GMA for addon {addonId}: {ex.Message}", "EnsureManagedGmaAvailable");
                        return null;
                    }
                }

	        private void EnsureGmaFileLinkedOrCopied(string addonId, string destinationPath, string sourceGmaPath, string context)
	        {
	            try
	            {
	                ValidatePath(sourceGmaPath, "sourceGmaPath");
	                ValidatePath(destinationPath, "destinationPath");
	
	                if (!LooksLikeGmaFile(sourceGmaPath))
	                {
	                    errorHandler.HandleWarning($"Source GMA for addon {addonId} is invalid or missing: {sourceGmaPath}", context);
	                    return;
	                }
	
	                var destinationDirectory = Path.GetDirectoryName(destinationPath);
	                if (!string.IsNullOrEmpty(destinationDirectory) && !Directory.Exists(destinationDirectory))
	                {
	                    Directory.CreateDirectory(destinationDirectory);
	                }
	
	                var sameDrive = AreSameDrive(sourceGmaPath, destinationPath);
	
	                if (File.Exists(destinationPath))
	                {
	                    bool isOk = false;
	                    if (LooksLikeGmaFile(destinationPath))
	                    {
	                        if (sameDrive)
	                        {
	                            isOk = IsHardLink(destinationPath, sourceGmaPath);
	                        }
	                        else
	                        {
	                            isOk = new FileInfo(destinationPath).Length == new FileInfo(sourceGmaPath).Length;
	                        }
	                    }
	
	                    if (isOk)
	                    {
	                        return;
	                    }
	
	                    try
	                    {
	                        File.SetAttributes(destinationPath, FileAttributes.Normal);
	                    }
	                    catch (Exception ex)
	                    {
	                        errorHandler.HandleWarning(
	                            $"Failed to normalize attributes for destination GMA {destinationPath}: {ex.Message}",
	                            context);
	                    }
	                    File.Delete(destinationPath);
	                }
	
                    if (sameDrive)
                    {
                        if (!CreateHardLinkSafe(destinationPath, sourceGmaPath))
                        {
                            if (!StrictLinkMode)
                            {
                                errorHandler.HandleWarning($"Failed to create hard link for {Path.GetFileName(destinationPath)}; copying file instead.", context);
                            }
                            CopyFileForLinkFallback(addonId, sourceGmaPath, destinationPath, context);
                        }
                    }
                    else
                    {
                        CopyFileForLinkFallback(addonId, sourceGmaPath, destinationPath, context);
                    }
                }
                catch (Exception ex)
                {
                    if (ex is StrictLinkModeException)
                    {
                        throw;
                    }

                    errorHandler.HandleWarning($"Failed to ensure GMA file for addon {addonId}: {ex.Message}", context);
                }
            }
	
	        private void EnsureCacheStructureForGma(string addonId, string sourceGmaPath)
	        {
	            if (string.IsNullOrEmpty(gmodCachePath))
	            {
	                return;
	            }
	
	            string cacheGmaPath = Path.Combine(gmodCachePath, $"{addonId}.gma");
	            ValidatePath(cacheGmaPath, "cacheGmaPath");
	
	            EnsureGmaFileLinkedOrCopied(addonId, cacheGmaPath, sourceGmaPath, "EnableGmaAddon");
	        }

        private void RemoveWorkshopPresence(string addonId, bool isGmaAddon)
        {
            string workshopAddonPath = Path.Combine(workshopPath, addonId);
            ValidatePath(workshopAddonPath, "workshopAddonPath");

            try
            {
                if (Directory.Exists(workshopAddonPath))
                {
                    if (junctionService.IsJunction(workshopAddonPath))
                    {
                        junctionService.RemoveJunction(workshopAddonPath);
                    }
                    else if (File.Exists(Path.Combine(workshopAddonPath, $"{addonId}.gma")))
                    {
                        // Hard link or copy case
                        var gmaFile = Path.Combine(workshopAddonPath, $"{addonId}.gma");
                        File.Delete(gmaFile);
                        // Clean up empty directory
                        if (!Directory.EnumerateFileSystemEntries(workshopAddonPath).Any())
                        {
                            Directory.Delete(workshopAddonPath, true);
                        }
                    }
                    else if (File.Exists(Path.Combine(workshopAddonPath, ".gam_disabled")))
                    {
                        Directory.Delete(workshopAddonPath, true);
                    }
                    else
                    {
                        // Unknown directory type: move aside to avoid Steam seeing active content
                        var backup = workshopAddonPath + "_disabled_" + Guid.NewGuid().ToString("N").Substring(0, 6);
                        Directory.Move(workshopAddonPath, backup);
                    }
                }
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning($"Failed to remove workshop entry for {addonId}: {ex.Message}", "RemoveWorkshopPresence");
            }

	            if (isGmaAddon)
	            {
	                try
	                {
	                    if (!string.IsNullOrEmpty(gmodCachePath))
	                    {
	                        var cacheGma = Path.Combine(gmodCachePath, $"{addonId}.gma");
	                        if (File.Exists(cacheGma))
                        {
                            File.Delete(cacheGma);
                        }

                        var cacheCache = Path.Combine(gmodCachePath, $"{addonId}.cache");
                        if (File.Exists(cacheCache) && LooksLikeGmaFile(cacheCache))
                        {
                            File.Delete(cacheCache);
                        }
                    }
                }
                catch (Exception ex)
                {
                    errorHandler.HandleWarning($"Failed to clean cache copy for {addonId}: {ex.Message}", "RemoveWorkshopPresence");
                }
            }
        }

        private void CreateDisabledStub(string workshopRoot, string addonId)
        {
            try
            {
                var stubPath = Path.Combine(workshopRoot, addonId);
                ValidatePath(stubPath, "stubPath");

                if (!Directory.Exists(stubPath))
                {
                    Directory.CreateDirectory(stubPath);
                }

                var markerPath = Path.Combine(stubPath, ".gam_disabled");
                File.WriteAllText(markerPath, "disabled by GmodAddonManager");
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning($"Failed to create disabled stub for addon {addonId}: {ex.Message}", "CreateDisabledStub");
            }
        }

        private void TryRestoreMovedAsideWorkshopFolder(string addonId)
        {
            try
            {
                string workshopAddonPath = Path.Combine(workshopPath, addonId);
                ValidatePath(workshopAddonPath, "workshopAddonPath");

                if (Directory.Exists(workshopAddonPath) && !File.Exists(Path.Combine(workshopAddonPath, ".gam_disabled")))
                {
                    return;
                }

                var candidates = new List<string>();
                candidates.AddRange(Directory.GetDirectories(workshopPath, addonId + "_disabled_*"));
                candidates.AddRange(Directory.GetDirectories(workshopPath, addonId + "_backup_*"));

                if (candidates.Count == 0)
                {
                    return;
                }

                string bestCandidate = candidates
                    .OrderByDescending(path =>
                    {
                        try { return Directory.GetLastWriteTimeUtc(path); } catch { return DateTime.MinValue; }
                    })
                    .First();

                RemoveDisabledStub(workshopPath, addonId);
                if (Directory.Exists(workshopAddonPath))
                {
                    return;
                }

                Directory.Move(bestCandidate, workshopAddonPath);
                errorHandler.HandleInfo(
                    $"Restored workshop folder for addon {addonId} from {Path.GetFileName(bestCandidate)}",
                    "RestoreWorkshopBackup");
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning(
                    $"Failed to restore workshop folder for addon {addonId}: {ex.Message}",
                    "RestoreWorkshopBackup");
            }
        }

	        private void EnsureWorkshopStructureForGma(string addonId, string sourceGmaPath)
	        {
	            ValidatePath(sourceGmaPath, "sourceGmaPath");

	            try
	            {
	                string addonDirectory = Path.Combine(workshopPath, addonId);
	                ValidatePath(addonDirectory, "addonDirectory");

	                if (!Directory.Exists(addonDirectory))
	                {
	                    Directory.CreateDirectory(addonDirectory);
	                }
	                else if (File.GetAttributes(addonDirectory).HasFlag(FileAttributes.ReparsePoint))
	                {
	                    // Ensure a normal directory for GMA-style workshop layout.
	                    try
	                    {
	                        if (junctionService.IsJunction(addonDirectory))
	                        {
	                            junctionService.RemoveJunction(addonDirectory);
	                        }
	                    }
	                    catch (Exception ex)
	                    {
	                        errorHandler.HandleWarning($"Failed to normalize workshop directory for addon {addonId}: {ex.Message}", "EnableGmaAddon");
	                    }

	                    if (!Directory.Exists(addonDirectory))
	                    {
	                        Directory.CreateDirectory(addonDirectory);
	                    }
	                }

	                string workshopGmaPath = Path.Combine(addonDirectory, $"{addonId}.gma");
	                ValidatePath(workshopGmaPath, "workshopGmaPath");

	                EnsureGmaFileLinkedOrCopied(addonId, workshopGmaPath, sourceGmaPath, "EnableGmaAddon");
	            }
	            catch (Exception ex)
	            {
	                errorHandler.HandleWarning($"Failed to ensure workshop GMA for addon {addonId}: {ex.Message}", "EnableGmaAddon");
	            }
	        }
        
        /// <summary>
        /// Remove stub directory before enabling an addon
        /// </summary>
        private bool RemoveDisabledStub(string workshopPath, string addonId)
        {
            bool removed = false;
            try
            {
                string stubPath = Path.Combine(workshopPath, addonId);
                string markerPath = Path.Combine(stubPath, ".gam_disabled");
                
                // Only remove if it's a GAM stub directory
                if (Directory.Exists(stubPath) && File.Exists(markerPath))
                {
                    foreach (var file in Directory.GetFiles(stubPath))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                    }
                    
                    Directory.Delete(stubPath, true);
                    errorHandler.HandleInfo($"Removed stub directory for addon {addonId}", "RemoveDisabledStub");
                    removed = true;
                }
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning($"Failed to remove stub for addon {addonId}: {ex.Message}", "RemoveDisabledStub");
                // Don't throw - continue with enable operation
            }
            return removed;
        }

        public List<string> GetEnabledAddons()
        {
            var enabledAddons = new List<string>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            
            // Check workshop folder for junctions
            var directories = Directory.GetDirectories(workshopPath)
                .Where(d => !Path.GetFileName(d).StartsWith("."));

            foreach (var directory in directories)
            {
                if (junctionService.IsJunction(directory))
                {
                    var addonId = Path.GetFileName(directory);
                    if (seenIds.Add(addonId))
                    {
                        enabledAddons.Add(addonId);
                    }
                }
            }

            // Check cache folder for GMA files
            if (!string.IsNullOrEmpty(gmodCachePath) && Directory.Exists(gmodCachePath))
            {
                var gmaFiles = Directory.GetFiles(gmodCachePath, "*.gma");
                foreach (var gmaFile in gmaFiles)
                {
                    string addonId = Path.GetFileNameWithoutExtension(gmaFile);
                    if (configuration.AddonMetadata.ContainsKey(addonId))
                    {
                        if (seenIds.Add(addonId))
                        {
                            enabledAddons.Add(addonId);
                        }
                    }
                }
            }

            if (EnableLocalAddonManagement)
            {
                foreach (var addon in configuration.AddonMetadata.Values.Where(a => a.IsLocal))
                {
                    if (IsLocalMountPresent(addon) && seenIds.Add(addon.Id))
                    {
                        enabledAddons.Add(addon.Id);
                    }
                }
            }

            return enabledAddons;
        }

        public AddonStateSnapshot CaptureState()
        {
            if (DisableMode == DisableMode.Soft && gmodAddonStateStore != null)
            {
                AddonMountSnapshot mountSnapshot;
                try
                {
                    mountSnapshot = gmodAddonStateStore.ReadSnapshot();
                }
                catch (Exception ex)
                {
                    errorHandler.HandleWarning(
                        $"Failed to read actual GMod addon state: {ex.Message}",
                        "CaptureState");
                    return BuildSnapshot(
                        new Dictionary<string, bool>(StringComparer.Ordinal),
                        "actual:addonnomount.txt:unavailable");
                }

                if (!mountSnapshot.IsValidFormat)
                {
                    errorHandler.HandleWarning(
                        "GMod addonnomount.txt has an invalid format; actual state is unknown.",
                        "CaptureState");
                    return BuildSnapshot(
                        new Dictionary<string, bool>(StringComparer.Ordinal),
                        "actual:addonnomount.txt:invalid");
                }

                var disabledIds = new HashSet<string>(
                    mountSnapshot.DisabledIds,
                    StringComparer.Ordinal);
                var softSnapshotStates = new Dictionary<string, bool>(StringComparer.Ordinal);
                var actualStateIds = new HashSet<string>(
                    configuration.AddonMetadata.Keys.Where(id => id != "*"),
                    StringComparer.Ordinal);
                if (TryGetSubscribedAddonIdSet("CaptureState", out var subscribedAddonIds))
                {
                    actualStateIds.UnionWith(subscribedAddonIds);
                }
                else
                {
                    actualStateIds.UnionWith(
                        configuration.KnownSubscribedAddonIds
                            .Where(IsWorkshopNumericId));
                }

                foreach (var addonId in actualStateIds
                             .OrderBy(id => id, StringComparer.Ordinal))
                {
                    if (!ShouldIncludeAddonInState(addonId))
                    {
                        continue;
                    }

                    if (IsLocalAddonId(addonId))
                    {
                        if (configuration.AddonMetadata.TryGetValue(addonId, out var addon))
                        {
                            softSnapshotStates[addonId] = IsLocalMountPresent(addon);
                        }
                        else
                        {
                            softSnapshotStates[addonId] = false;
                        }
                    }
                    else
                    {
                        softSnapshotStates[addonId] = !disabledIds.Contains(addonId);
                    }
                }

                return BuildSnapshot(softSnapshotStates, "actual:addonnomount.txt");
            }

            var enabledAddons = new HashSet<string>(GetEnabledAddons(), StringComparer.Ordinal);
            var states = new Dictionary<string, bool>(StringComparer.Ordinal);

            foreach (var addonId in configuration.AddonMetadata.Keys
                         .Where(id => id != "*")
                         .OrderBy(id => id, StringComparer.Ordinal))
            {
                if (!ShouldIncludeAddonInState(addonId))
                {
                    continue;
                }

                if (IsLocalAddonId(addonId))
                {
                    if (configuration.AddonMetadata.TryGetValue(addonId, out var addon))
                    {
                        states[addonId] = IsLocalMountPresent(addon);
                    }
                    else
                    {
                        states[addonId] = false;
                    }
                }
                else
                {
                    states[addonId] = enabledAddons.Contains(addonId);
                }
            }

            return BuildSnapshot(states, "actual");
        }

        public AddonStateSnapshot CaptureExpectedStateSnapshot()
        {
            var scope = GetExpectedScopeLabel(assetSpecific: false);
            return BuildSnapshot(BuildExpectedStates(), scope);
        }

        public IReadOnlyDictionary<string, bool> GetFinalAddonStates()
        {
            return BuildExpectedStates();
        }

        public ResolvedAddonState GetResolvedAddonState(string addonId)
        {
            var runtimeTargets = GetRuntimeTargetAddonIds("GetResolvedAddonState");
            return assetStateResolver.Resolve(addonId, configuration.Assets, runtimeTargets);
        }

        public IReadOnlyDictionary<string, ResolvedAddonState> GetResolvedAddonStates()
        {
            var runtimeTargets = GetRuntimeTargetAddonIds("GetResolvedAddonStates");
            return runtimeTargets.ToDictionary(
                addonId => addonId,
                addonId => assetStateResolver.Resolve(addonId, configuration.Assets, runtimeTargets),
                StringComparer.Ordinal);
        }

        public bool? GetActualAddonEnabledState(string addonId)
        {
            if (string.IsNullOrWhiteSpace(addonId))
            {
                return null;
            }

            if (DisableMode == DisableMode.Soft && gmodAddonStateStore != null)
            {
                return gmodAddonStateStore.GetEnabled(addonId);
            }

            return configuration.AddonMetadata.TryGetValue(addonId, out var addon)
                ? addon.IsEnabled
                : (bool?)null;
        }

        private bool TryCaptureCurrentSubscribedRuntimeStates(
            out Dictionary<string, bool> states)
        {
            states = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (gmodAddonStateStore == null ||
                !TryGetSubscribedAddonIdSet(
                    "CaptureCurrentSubscribedRuntimeStates",
                    out var subscribedIds))
            {
                return false;
            }

            AddonMountSnapshot snapshot;
            try
            {
                snapshot = gmodAddonStateStore.ReadSnapshot();
            }
            catch
            {
                return false;
            }

            if (!snapshot.IsValidFormat)
            {
                return false;
            }

            var disabledIds = new HashSet<string>(
                snapshot.DisabledIds,
                StringComparer.Ordinal);
            states = subscribedIds.ToDictionary(
                id => id,
                id => !disabledIds.Contains(id),
                StringComparer.Ordinal);
            return true;
        }

        public string ComputeStateHash(AddonStateSnapshot snapshot)
        {
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(snapshot.NormalizedState);
            var hash = sha.ComputeHash(bytes);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        private Dictionary<string, bool> BuildExpectedStates()
        {
            return BuildExpectedStatesForAssets(configuration.Assets);
        }

        private Dictionary<string, bool> BuildExpectedStatesForAssets(
            IReadOnlyList<Asset> enabledAssets)
        {
            var subscribedAddonIds = GetRuntimeTargetAddonIds("BuildExpectedStates");
            return BuildExpectedStatesForAssets(enabledAssets, subscribedAddonIds);
        }

        private Dictionary<string, bool> BuildExpectedStatesForAssets(
            IReadOnlyList<Asset> enabledAssets,
            ISet<string> subscribedAddonIds)
        {
            var states = new Dictionary<string, bool>(StringComparer.Ordinal);

            foreach (var addonId in subscribedAddonIds.OrderBy(id => id, StringComparer.Ordinal))
            {
                if (!ShouldIncludeAddonInState(addonId))
                {
                    continue;
                }

                states[addonId] = assetStateResolver
                    .Resolve(addonId, enabledAssets, subscribedAddonIds)
                    .DesiredEnabled;
            }

            return states;
        }

        private HashSet<string> GetRuntimeTargetAddonIds(string operationName)
        {
            if (TryGetSubscribedAddonIdSet(operationName, out var subscribedAddonIds))
            {
                return subscribedAddonIds;
            }

            errorHandler.HandleWarning(
                "Steam subscription state is unavailable; using the last known subscription set for read-only state display.",
                operationName);
            return new HashSet<string>(
                configuration.KnownSubscribedAddonIds
                    .Where(IsWorkshopNumericId),
                StringComparer.Ordinal);
        }

        private bool ShouldIncludeAddonInState(string addonId)
        {
            if (string.IsNullOrWhiteSpace(addonId) || addonId == "*")
            {
                return false;
            }

            if (IsLocalAddonId(addonId))
            {
                return EnableLocalAddonManagement;
            }

            return true;
        }

        private static bool IsWorkshopNumericId(string addonId)
        {
            return !string.IsNullOrWhiteSpace(addonId) && ulong.TryParse(addonId, out _);
        }

        private AddonStateSnapshot? CaptureExpectedStateSnapshotForAsset(string? assetId)
        {
            if (string.IsNullOrWhiteSpace(assetId))
            {
                return null;
            }

            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId);
            if (asset == null)
            {
                return null;
            }

            var selectedAssets = configuration.Assets.Where(a => a.Id == assetId).ToList();
            var states = BuildExpectedStatesForAssets(selectedAssets);
            return BuildSnapshot(states, GetExpectedScopeLabel(assetSpecific: true));
        }

        private AddonStateSnapshot BuildSnapshot(Dictionary<string, bool> states, string? source)
        {
            var sb = new StringBuilder();
            bool first = true;

            foreach (var kvp in states.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
            {
                if (!first)
                {
                    sb.Append('\n');
                }

                sb.Append(kvp.Key).Append('=').Append(kvp.Value ? '1' : '0');
                first = false;
            }

            return new AddonStateSnapshot(
                new Dictionary<string, bool>(states, StringComparer.Ordinal),
                sb.ToString(),
                DateTime.UtcNow,
                source);
        }

        private string GetExpectedScopeLabel(bool assetSpecific)
        {
            var suffix = DisableMode == DisableMode.Soft ? "addonnomount.txt" : "actual";
            return assetSpecific ? $"expected:asset:{suffix}" : $"expected:{suffix}";
        }

        private bool? GetGmodRunning()
        {
            return GmodRunningProvider?.Invoke();
        }

        internal bool IsGmodCurrentlyRunning()
        {
            return GetGmodRunning() ?? SteamProcessChecker.IsGmodRunning();
        }

        private bool DeferRuntimeApplyIfGmodRunning(
            bool allowConflictSupersede = false)
        {
            if (!IsGmodCurrentlyRunning())
            {
                return false;
            }

            var conflictedOperationId = allowConflictSupersede &&
                                        configuration.PendingGamRuntimeWrite?.ConflictDetected == true
                ? configuration.PendingGamRuntimeWrite.OperationId
                : null;
            var queue = QueueRuntimeApplyProvider;
            queue?.Invoke();
            if (queue != null && !string.IsNullOrWhiteSpace(conflictedOperationId))
            {
                FinalizeRuntimeAttributionConflictSynchronouslyCore(
                    conflictedOperationId);
            }
            return true;
        }

        private int? GetPendingChangeCount()
        {
            return PendingChangeCountProvider?.Invoke();
        }

        private void LogExperimentEvent(
            string actionType,
            string eventScope = "system",
            string? targetId = null,
            string? result = null,
            long? durationMs = null,
            string? beforeHash = null,
            string? afterHash = null,
            string? expectedHash = null,
            string? errorCode = null,
            string? operationId = null,
            string? assetId = null,
            bool? taskSuccess = null,
            string? finalHash = null,
            string? blMethod = null,
            string? note = null,
            ExperimentEventMetrics? metrics = null,
            string? taskIdOverride = null,
            string? assetLabel = null,
            string? assetDisplayName = null,
            List<string>? fromAssetIds = null,
            List<string>? fromAssetLabels = null,
            List<string>? fromAssetDisplayNames = null,
            string? toAssetId = null,
            string? toAssetLabel = null,
            string? toAssetDisplayName = null,
            string? parentOperationId = null,
            string? stateHashScope = null,
            string? expectedHashScope = null,
            bool? stateChanged = null,
            string? fromAssetResolveMethod = null,
            string? toAssetResolveMethod = null)
        {
            var pendingCount = GetPendingChangeCount();
            var pendingQueued = pendingCount.HasValue ? pendingCount.Value > 0 : (bool?)null;

            eventLogger.LogEvent(
                actionType,
                eventScope: eventScope,
                targetId: targetId,
                result: result,
                durationMs: durationMs,
                beforeHash: beforeHash,
                afterHash: afterHash,
                expectedHash: expectedHash,
                errorCode: errorCode,
                operationId: operationId,
                assetId: assetId,
                taskSuccess: taskSuccess,
                finalHash: finalHash,
                blMethod: blMethod,
                note: note,
                metrics: metrics,
                gmodRunning: GetGmodRunning(),
                pendingChangeQueued: pendingQueued,
                pendingQueueLength: pendingCount,
                taskIdOverride: taskIdOverride,
                assetLabel: assetLabel,
                assetDisplayName: assetDisplayName,
                fromAssetIds: fromAssetIds,
                fromAssetLabels: fromAssetLabels,
                fromAssetDisplayNames: fromAssetDisplayNames,
                toAssetId: toAssetId,
                toAssetLabel: toAssetLabel,
                toAssetDisplayName: toAssetDisplayName,
                parentOperationId: parentOperationId,
                stateHashScope: stateHashScope,
                expectedHashScope: expectedHashScope,
                stateChanged: stateChanged,
                fromAssetResolveMethod: fromAssetResolveMethod,
                toAssetResolveMethod: toAssetResolveMethod);
        }

        private void LogLinkFallbackCopy(string addonId, string context)
        {
            LogExperimentEvent(
                "LinkFallbackCopy",
                eventScope: "system",
                targetId: addonId,
                result: "success",
                errorCode: $"copy_used:{context}");
        }

        private void LogStrictLinkViolation(string addonId, string context)
        {
            LogExperimentEvent(
                "StrictLinkViolation",
                eventScope: "system",
                targetId: addonId,
                result: "fail",
                errorCode: $"strict_link_copy_blocked:{context}");
        }

        private void CreateJunctionWithMetrics(string junctionPath, string targetPath)
        {
            if (DisableMode == DisableMode.Soft)
            {
                return;
            }

            try
            {
                if (Directory.Exists(junctionPath) && junctionService.IsJunction(junctionPath))
                {
                    var existingTarget = junctionService.GetJunctionTarget(junctionPath);
                    var normalizedTarget = Path.GetFullPath(targetPath);
                    if (string.Equals(existingTarget, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
            }
            catch
            {
                // Fall through and attempt creation; metrics will be recorded on success.
            }

            junctionService.CreateJunction(junctionPath, targetPath);
            linkMetricsContext.Value?.RecordJunction();
        }

        private void CopyFileForLinkFallback(string addonId, string source, string destination, string context, bool overwrite = true)
        {
            if (StrictLinkMode)
            {
                LogStrictLinkViolation(addonId, context);
                throw new StrictLinkModeException($"StrictLinkMode blocked File.Copy for addon {addonId} ({context}).");
            }

            LogLinkFallbackCopy(addonId, context);
            long bytes = 0;
            try
            {
                if (File.Exists(source))
                {
                    bytes = new FileInfo(source).Length;
                }
            }
            catch
            {
                bytes = 0;
            }

            File.Copy(source, destination, overwrite);
            linkMetricsContext.Value?.RecordCopy(bytes);
        }

        private static StrictLinkModeException? FindStrictLinkModeException(Exception ex)
        {
            if (ex is StrictLinkModeException strict)
            {
                return strict;
            }

            if (ex is AggregateException aggregate)
            {
                foreach (var inner in aggregate.Flatten().InnerExceptions)
                {
                    var found = FindStrictLinkModeException(inner);
                    if (found != null)
                    {
                        return found;
                    }
                }
            }

            if (ex.InnerException != null)
            {
                return FindStrictLinkModeException(ex.InnerException);
            }

            return null;
        }

        public async Task LoadConfigurationAsync()
        {
            var backupPath = configPath + ".bak";
            if (File.Exists(configPath) || File.Exists(backupPath))
            {
                try
                {
                    (string Json, JObject Raw, Configuration Value) loaded;
                    try
                    {
                        loaded = await ReadConfigurationFileAsync(configPath);
                    }
                    catch (UnsupportedConfigurationSchemaException)
                    {
                        // A newer GAM owns this profile. Falling back to an older
                        // backup would silently discard its fields on the next save.
                        throw;
                    }
                    catch (Exception primaryException) when (File.Exists(backupPath))
                    {
                        try
                        {
                            loaded = await ReadConfigurationFileAsync(backupPath);
                        }
                        catch (UnsupportedConfigurationSchemaException)
                        {
                            throw;
                        }
                        catch (Exception backupException)
                        {
                            throw new InvalidOperationException(
                                "Both the primary configuration and its backup are unreadable.",
                                new AggregateException(primaryException, backupException));
                        }

                        await RestoreConfigurationBackupAsync(backupPath);
                        errorHandler.HandleWarning(
                            "Recovered configuration from config.json.bak; the invalid primary was preserved.",
                            "LoadConfiguration");
                    }

                    var jsonObj = loaded.Raw;
                    configuration = loaded.Value;

                    // This marker was introduced after schema v2. An existing v2
                    // profile without it is not a new profile and must not run the
                    // one-time addonnomount import.
                    var missingInitialRuntimeImportMarker =
                        jsonObj.Property(
                            "initialRuntimeImportCompleted",
                            StringComparison.OrdinalIgnoreCase) == null;

                    if (configurationMigrationService.RequiresMigration(jsonObj))
                    {
                        var migrationBackupPath =
                            configPath + $".pre-schema-{Configuration.CurrentSchemaVersion}.bak";
                        if (!File.Exists(migrationBackupPath))
                        {
                            File.Copy(configPath, migrationBackupPath, overwrite: false);
                        }

                        var migrationResult = configurationMigrationService.Migrate(
                            jsonObj,
                            configuration,
                            removeLegacyJunctionAsset: DisableMode == DisableMode.Soft);
                        errorHandler.HandleInfo(
                            $"Migrated configuration to schema {Configuration.CurrentSchemaVersion}. " +
                            $"Review assets: {migrationResult.NeedsReviewAssetIds.Count}.",
                            "LoadConfiguration");
                        await SaveConfigurationImmediatelyAsync();
                    }
                    else
                    {
                        configurationMigrationService.NormalizeCurrentSchema(
                            configuration,
                            removeLegacyJunctionAsset: DisableMode == DisableMode.Soft);
                        MigrateSystemAssetNames();

                        if (missingInitialRuntimeImportMarker)
                        {
                            configuration.InitialRuntimeImportCompleted = true;
                            configuration.InitialRuntimeImportCompletedAtUtc ??= DateTime.UtcNow;
                            await SaveConfigurationImmediatelyAsync();
                        }
                    }
                    
                    // Fix any invalid CurrentVersion values
                    FixInvalidCurrentVersions();
                    configuration.PathState ??= new PathState();
                }
                catch (Exception ex)
                {
                    errorHandler.HandleError(ex, "Failed to load configuration", ErrorSeverity.Error);
                    throw;
                }
            }
        }

        private async Task<(string Json, JObject Raw, Configuration Value)> ReadConfigurationFileAsync(
            string path)
        {
            var json = await Task.Run(() => File.ReadAllText(path, Encoding.UTF8));
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidOperationException("Configuration file is empty");
            }

            try
            {
                var raw = JObject.Parse(json);
                configurationMigrationService.EnsureSupportedSchema(raw);
                var value = JsonConvert.DeserializeObject<Configuration>(
                    json,
                    new JsonSerializerSettings
                    {
                        MissingMemberHandling = MissingMemberHandling.Ignore
                    }) ?? throw new InvalidOperationException("Configuration deserialized to null.");
                return (json, raw, value);
            }
            catch (UnsupportedConfigurationSchemaException)
            {
                throw;
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Invalid configuration file format: {ex.Message}", ex);
            }
        }

        private async Task RestoreConfigurationBackupAsync(string backupPath)
        {
            var tempPath = configPath + $".{Guid.NewGuid():N}.recovery.tmp";
            var corruptArchivePath =
                configPath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.bak";
            try
            {
                await Task.Run(() => ExecuteWithConfigurationPathMutex(() =>
                {
                    File.Copy(backupPath, tempPath, overwrite: false);
                    if (File.Exists(configPath))
                    {
                        File.Replace(tempPath, configPath, corruptArchivePath);
                    }
                    else
                    {
                        File.Move(tempPath, configPath);
                    }
                }));
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (Exception ex)
                {
                    errorHandler.HandleWarning(
                        $"Failed to clean up configuration recovery temp file: {ex.Message}",
                        "LoadConfiguration");
                }
            }
        }
        
        private void MigrateSystemAssetNames()
        {
            // Find and update Subscribe asset
            var subscribeAsset = configuration.Assets.FirstOrDefault(a =>
                a.IsSystem && (a.Name == "サブスクライブ" || a.Name == "サブスクライブアセット" || a.Name == "Subscribe" || a.Name == "Subscribe Asset"));
            if (subscribeAsset != null)
            {
                subscribeAsset.Name = "Subscribe Asset";
                if (string.IsNullOrEmpty(subscribeAsset.Id))
                {
                    subscribeAsset.Id = "subscribe-system-asset";
                }
            }
            
            // Find and update Junction asset  
            var junctionAsset = configuration.Assets.FirstOrDefault(a =>
                a.IsSystem && (a.Name == "ジャンクション" || a.Name == "Junction"));
            if (junctionAsset != null)
            {
                junctionAsset.Name = "Junction";
                if (string.IsNullOrEmpty(junctionAsset.Id) || junctionAsset.Id != "junction-system-asset")
                {
                    junctionAsset.Id = "junction-system-asset";
                }
            }
        }
        
        private void FixInvalidCurrentVersions()
        {
            foreach (var asset in configuration.Assets)
            {
                // CurrentVersionが-1の場合、0に修正
                if (asset.CurrentVersion == -1)
                {
                    // [AddonManager] Fixing invalid CurrentVersion -1 for asset '{asset.Name}' to 0
                    asset.CurrentVersion = 0;
                }
                
                // インポートベースラインがある場合でも、CurrentVersionは0以上であるべき
                if (asset.CurrentVersion < 0 ||
                    (asset.CurrentVersion > 0 &&
                     asset.VersionHistory.All(
                         version => version.Version != asset.CurrentVersion)))
                {
                    // Missing/invalid version labels must not survive normalization.
                    asset.CurrentVersion = 0;
                }
            }
        }

        public async Task SaveConfigurationAsync()
        {
            if (configuration != null)
            {
                lock (_saveLock)
                {
                    configuration.LastUpdated = DateTime.UtcNow;
                }
            }

            RequestSave();
            await Task.CompletedTask; // 非同期メソッドを維持
            }
        
        /// <summary>
        /// 設定を即座に保存（デバウンスを無視）
        /// </summary>
        public async Task SaveConfigurationImmediatelyAsync()
        {
            errorHandler.HandleInfo($"SaveConfigurationImmediatelyAsync: Starting immediate save. Current assets count: {configuration.Assets.Count}", "SaveConfiguration");
            
            // Initialize WorkshopIconResolver
            lock (_saveLock)
            {
                _saveDebounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _saveRequested = false;
            }
            
            // Initialize WorkshopIconResolver
            await SaveConfigurationInternalAsync();
            errorHandler.HandleInfo("SaveConfigurationImmediatelyAsync: Save completed", "SaveConfiguration");
        }
        
        /// <summary>
        // Initialize WorkshopIconResolver
        /// </summary>
        private void RequestSave()
        {
            lock (_saveLock)
            {
                _saveRequested = true;
                _saveDebounceTimer.Change(_saveDebounceMilliseconds, Timeout.Infinite);
            }
        }

        /// <summary>
        // Initialize WorkshopIconResolver
        private void OnSaveDebounceTimer(object? state)
        {
            _ = ExecutePendingSaveAsyncSafe();
        }

        private async Task ExecutePendingSaveAsyncSafe()
        {
            try
            {
                await ExecutePendingSaveAsync();
            }
            catch (Exception ex)
            {
                errorHandler.HandleError(ex, "SaveConfiguration", ErrorSeverity.Error);
            }
        }

        private async Task ExecutePendingSaveAsync()
        {
            lock (_saveLock)
            {
                if (!_saveRequested) return;
                _saveRequested = false;
            }
            await SaveConfigurationInternalAsync();
        }
        
        /// <summary>
        // Initialize WorkshopIconResolver
        private async Task SaveConfigurationInternalAsync()
        {
            await _configurationSaveGate.WaitAsync();
            try
            {
                await SaveConfigurationCoreAsync();
            }
            finally
            {
                _configurationSaveGate.Release();
            }
        }

        private async Task SaveConfigurationCoreAsync()
        {
            const int maxRetries = 3;
            string? json = null;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    // Initialize WorkshopIconResolver
                    lock (_saveLock)
                    {
                        var now = DateTime.UtcNow;
                        configuration.LastUpdated = now;

                        // Create a manual snapshot to avoid "collection was modified" errors
                        // Take snapshots of keys/values first to minimize race window
                        var addonMetadataSnapshot = configuration.AddonMetadata.Keys.ToArray()
                            .Where(k => configuration.AddonMetadata.ContainsKey(k))
                            .ToDictionary(k => k, k =>
                            {
                                configuration.AddonMetadata.TryGetValue(k, out var v);
                                return v;
                            })
                            .Where(kvp => kvp.Value != null)
                            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                        var junctionHistorySnapshot = configuration.JunctionHistory.Keys.ToArray()
                            .Where(k => configuration.JunctionHistory.ContainsKey(k))
                            .ToDictionary(k => k, k =>
                            {
                                configuration.JunctionHistory.TryGetValue(k, out var v);
                                return v?.ToList() ?? new List<string>();
                            });

                        var configCopy = new Configuration
                        {
                            SchemaVersion = configuration.SchemaVersion,
                            Version = configuration.Version,
                            LastUpdated = now,
                            Assets = configuration.Assets.ToList(),
                            AddonMetadata = addonMetadataSnapshot,
                            JunctionHistory = junctionHistorySnapshot,
                            PathState = configuration.PathState ?? new PathState(),
                            InitialRuntimeImportCompleted = configuration.InitialRuntimeImportCompleted,
                            InitialRuntimeImportCompletedAtUtc = configuration.InitialRuntimeImportCompletedAtUtc,
                            SubscriptionBaselineInitialized = configuration.SubscriptionBaselineInitialized,
                            KnownSubscribedAddonIds = configuration.KnownSubscribedAddonIds?.ToList() ?? new List<string>(),
                            SubscriptionFirstSeenAtUtc =
                                configuration.SubscriptionFirstSeenAtUtc != null
                                    ? new Dictionary<string, DateTime>(
                                        configuration.SubscriptionFirstSeenAtUtc,
                                        StringComparer.Ordinal)
                                    : new Dictionary<string, DateTime>(StringComparer.Ordinal),
                            RetainMissingAssetReferences = configuration.RetainMissingAssetReferences,
                            GamAppliedRuntimeBaselineInitialized =
                                configuration.GamAppliedRuntimeBaselineInitialized,
                            LastGamAppliedAddonStates =
                                new Dictionary<string, bool>(
                                    configuration.LastGamAppliedAddonStates ??
                                    new Dictionary<string, bool>(),
                                    StringComparer.Ordinal),
                            LastGamAppliedRuntimeAtUtc =
                                configuration.LastGamAppliedRuntimeAtUtc,
                            LastGamAppliedStateStorePath =
                                configuration.LastGamAppliedStateStorePath,
                            GmodObservationBaselineInitialized =
                                configuration.GmodObservationBaselineInitialized,
                            LastObservedGmodAddonStates =
                                new Dictionary<string, bool>(
                                    configuration.LastObservedGmodAddonStates ??
                                    new Dictionary<string, bool>(),
                                    StringComparer.Ordinal),
                            LastObservedGmodRuntimeAtUtc =
                                configuration.LastObservedGmodRuntimeAtUtc,
                            LastObservedGmodStateStorePath =
                                configuration.LastObservedGmodStateStorePath,
                            PendingGamRuntimeWrite =
                                configuration.PendingGamRuntimeWrite == null
                                    ? null
                                    : new PendingGamRuntimeWrite
                                    {
                                        OperationId = configuration.PendingGamRuntimeWrite.OperationId,
                                        TargetStates = new Dictionary<string, bool>(
                                            configuration.PendingGamRuntimeWrite.TargetStates ??
                                            new Dictionary<string, bool>(),
                                            StringComparer.Ordinal),
                                        PreviousStates = new Dictionary<string, bool>(
                                            configuration.PendingGamRuntimeWrite.PreviousStates ??
                                            new Dictionary<string, bool>(),
                                            StringComparer.Ordinal),
                                        CreatedAtUtc = configuration.PendingGamRuntimeWrite.CreatedAtUtc,
                                        StateStorePath = configuration.PendingGamRuntimeWrite.StateStorePath,
                                        ConflictDetected =
                                            configuration.PendingGamRuntimeWrite.ConflictDetected
                                    },
                            GmodAttributionMigrationPending =
                                configuration.GmodAttributionMigrationPending
                        };

                        // Initialize WorkshopIconResolver
                        json = JsonConvert.SerializeObject(configCopy, Formatting.Indented);
                    }
                    break; // Success, exit retry loop
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("Collection was modified"))
                {
                    if (attempt < maxRetries - 1)
                    {
                        errorHandler.HandleWarning($"Configuration snapshot failed (attempt {attempt + 1}/{maxRetries}), retrying...", "SaveConfiguration");
                        await Task.Delay(50 * (attempt + 1)); // Progressive delay
                        continue;
                    }
                    throw; // Rethrow on final attempt
                }
            }

            if (string.IsNullOrEmpty(json))
            {
                var snapshotError = new InvalidOperationException(
                    "Failed to create configuration snapshot.");
                errorHandler.HandleError(
                    snapshotError,
                    "Failed to save configuration - could not create snapshot",
                    ErrorSeverity.Error);
                throw snapshotError;
            }

            var tempPath = configPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                var backupPath = configPath + ".bak";

                await Task.Run(() => ExecuteWithConfigurationPathMutex(() =>
                {
                    File.WriteAllText(tempPath, json);

                    if (File.Exists(configPath))
                    {
                        File.Replace(tempPath, configPath, backupPath);
                    }
                    else
                    {
                        File.Move(tempPath, configPath);
                    }
                }));

                errorHandler.HandleInfo($"Configuration saved successfully (atomic). Assets count: {configuration.Assets.Count}", "SaveConfiguration");
            }
            catch (Exception ex)
            {
                errorHandler.HandleError(ex, "Failed to save configuration", ErrorSeverity.Error);
                throw;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (Exception)
                {
                    // Failed to clean up temp file - not critical
                    errorHandler.HandleInfo($"Failed to clean up temp file", "SaveConfiguration");
                }
            }
        }

        private void ExecuteWithConfigurationPathMutex(Action action)
        {
            using var pathMutex = new Mutex(
                initiallyOwned: false,
                name: configurationPathMutexName);
            var acquired = false;
            try
            {
                try
                {
                    acquired = pathMutex.WaitOne(ConfigurationPathMutexTimeout);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }

                if (!acquired)
                {
                    throw new TimeoutException(
                        $"Timed out after {ConfigurationPathMutexTimeout.TotalSeconds:0} seconds " +
                        "while waiting to save the shared GAM configuration.");
                }

                action();
            }
            finally
            {
                if (acquired)
                {
                    pathMutex.ReleaseMutex();
                }
            }
        }

        private static string BuildConfigurationPathMutexName(string path)
        {
            var normalizedPath = Path.GetFullPath(path)
                .Trim()
                .ToUpperInvariant();
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalizedPath));
            var hex = BitConverter.ToString(hash).Replace("-", string.Empty);
            return "GmodAddonManager_Configuration_" + hex;
        }
        
        /// <summary>
        // Initialize WorkshopIconResolver
        public int SaveDebounceMilliseconds
        {
            get => _saveDebounceMilliseconds;
            set
            {
                if (value < 100) value = 100; // 最小100ms
                if (value > 10000) value = 10000; // 最大10秒
                _saveDebounceMilliseconds = value;
            }
        }

        public Configuration GetConfiguration()
        {
            return configuration;
        }

        public bool IsLocalAddonId(string addonId)
        {
            if (string.IsNullOrWhiteSpace(addonId))
            {
                return false;
            }

            if (configuration?.AddonMetadata != null &&
                configuration.AddonMetadata.TryGetValue(addonId, out var addon))
            {
                return addon.IsLocal;
            }

            return addonId.StartsWith("local_", StringComparison.OrdinalIgnoreCase);
        }

        public Dictionary<string, WorkshopAddon> GetAllAddons()
        {
            return configuration.AddonMetadata;
        }

        public bool AssetNameExists(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            var trimmedName = name.Trim();
            return configuration.Assets.Any(asset =>
                string.Equals(asset.Name, trimmedName, StringComparison.CurrentCultureIgnoreCase));
        }

        private static void ThrowIfProtectedSystemAsset(string? assetId)
        {
            if (GmodDisabledAddonReconciliationService.IsProtectedSystemAsset(assetId))
            {
                throw new InvalidOperationException(
                    $"Protected system asset cannot be edited: {assetId}");
            }
        }

        public void RenameAsset(string assetId, string newName)
        {
            ThrowIfProtectedSystemAsset(assetId);
            if (string.IsNullOrWhiteSpace(newName))
            {
                throw new ArgumentException("Asset name cannot be empty.", nameof(newName));
            }

            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId && !a.IsSystem);
            if (asset == null)
            {
                throw new InvalidOperationException($"Asset not found or is system asset: {assetId}");
            }

            var trimmed = newName.Trim();
            if (string.Equals(asset.Name, trimmed, StringComparison.CurrentCultureIgnoreCase))
            {
                return;
            }

            if (AssetNameExists(trimmed))
            {
                throw new InvalidOperationException($"Asset name already exists: {trimmed}");
            }

            undoManager.RecordAction(new UndoAction(
                UndoActionType.AssetRenamed,
                $"Renamed asset '{asset.Name}' to '{trimmed}'")
            {
                AssetId = asset.Id,
                AssetName = trimmed,
                PreviousAssetName = asset.Name
            });
            asset.Name = trimmed;
        }

        public async Task SetAssetFavoriteAsync(string assetId, bool isFavorite)
        {
            ThrowIfProtectedSystemAsset(assetId);
            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId && !a.IsSystem);
            if (asset == null)
            {
                throw new InvalidOperationException($"Custom asset not found: {assetId}");
            }

            if (asset.IsFavorite == isFavorite)
            {
                return;
            }

            var previousFavorite = asset.IsFavorite;
            var undoAction = new UndoAction(
                UndoActionType.AssetFavoriteChanged,
                $"{(isFavorite ? "Favorited" : "Unfavorited")} asset '{asset.Name}'")
            {
                AssetId = asset.Id,
                AssetName = asset.Name,
                PreviousFavoriteState = previousFavorite
            };
            undoManager.RecordAction(undoAction);
            asset.IsFavorite = isFavorite;
            try
            {
                await SaveConfigurationImmediatelyAsync();
            }
            catch
            {
                asset.IsFavorite = previousFavorite;
                undoManager.RemoveAction(undoAction);
                throw;
            }
        }

        public string? ResolveAssetImagePath(Asset asset)
        {
            if (asset == null || string.IsNullOrWhiteSpace(asset.ImagePath))
            {
                return null;
            }

            try
            {
                var candidate = asset.ImagePath.Trim();
                var fullPath = Path.IsPathRooted(candidate)
                    ? candidate
                    : Path.Combine(managerPath, candidate);

                ValidatePath(fullPath, "assetImagePath");
                return fullPath;
            }
            catch
            {
                return null;
            }
        }

        public string SetAssetImage(string assetId, byte[] pngBytes)
        {
            ThrowIfProtectedSystemAsset(assetId);
            if (pngBytes == null || pngBytes.Length == 0)
            {
                throw new ArgumentException("Image data cannot be empty.", nameof(pngBytes));
            }

            var asset = configuration.Assets.FirstOrDefault(a =>
                a.Id == assetId &&
                (!a.IsSystem || a.Id == SubscribeSystemAssetId));
            if (asset == null)
            {
                throw new InvalidOperationException($"Asset not found or is system asset: {assetId}");
            }

            var imageDir = GetAssetImageDirectory();
            var fileName = $"{assetId}.png";
            var fullPath = Path.Combine(imageDir, fileName);
            ValidatePath(fullPath, "assetImagePath");

            var previousPath = asset.ImagePath;
            byte[]? previousBytes = null;
            var resolvedPreviousPath = ResolveAssetImagePath(asset);
            if (!string.IsNullOrWhiteSpace(resolvedPreviousPath) && File.Exists(resolvedPreviousPath))
            {
                previousBytes = File.ReadAllBytes(resolvedPreviousPath);
            }

            File.WriteAllBytes(fullPath, pngBytes);

            asset.ImagePath = Path.Combine(AssetImageDirectoryName, fileName);
            undoManager.RecordAction(new UndoAction(
                UndoActionType.AssetImageChanged,
                $"Changed image for asset '{asset.Name}'")
            {
                AssetId = asset.Id,
                AssetName = asset.Name,
                PreviousImagePath = previousPath,
                PreviousImageBytes = previousBytes
            });
            return asset.ImagePath;
        }

        public string SetAssetImageFromFile(string assetId, string sourcePath, AssetImageCrop? crop)
        {
            return SetAssetImage(
                assetId,
                CreateAssetImagePngFromFile(sourcePath, crop));
        }

        private static byte[] CreateAssetImagePngFromFile(
            string sourcePath,
            AssetImageCrop? crop)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new ArgumentException("Source path cannot be empty.", nameof(sourcePath));
            }

            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("Source image file not found.", sourcePath);
            }

            using var bitmap = DecodeBitmapFromFile(sourcePath);
            if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                throw new InvalidOperationException("Failed to decode image.");
            }

            var cropRect = NormalizeCropRect(crop, bitmap.Width, bitmap.Height);

            using var cropped = new SKBitmap(cropRect.Width, cropRect.Height, bitmap.ColorType, bitmap.AlphaType);
            if (!bitmap.ExtractSubset(cropped, cropRect))
            {
                throw new InvalidOperationException("Failed to crop image.");
            }

            using var resized = new SKBitmap(AssetImageOutputSize, AssetImageOutputSize, SKColorType.Rgba8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(resized))
            using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
            {
                canvas.Clear(SKColors.Transparent);
                var cornerRadius = AssetImageOutputSize * AssetImageCornerRadiusRatio;
                using var clipPath = new SKPath();
                clipPath.AddRoundRect(new SKRect(0, 0, AssetImageOutputSize, AssetImageOutputSize), cornerRadius, cornerRadius);
                canvas.ClipPath(clipPath, SKClipOperation.Intersect, true);

                var scale = Math.Min(
                    (float)AssetImageOutputSize / cropped.Width,
                    (float)AssetImageOutputSize / cropped.Height);
                var drawWidth = cropped.Width * scale;
                var drawHeight = cropped.Height * scale;
                var left = (AssetImageOutputSize - drawWidth) / 2f;
                var top = (AssetImageOutputSize - drawHeight) / 2f;

                canvas.DrawBitmap(
                    cropped,
                    new SKRect(left, top, left + drawWidth, top + drawHeight),
                    paint);
            }

            using var data = resized.Encode(SKEncodedImageFormat.Png, 100);
            if (data == null)
            {
                throw new InvalidOperationException("Failed to encode image.");
            }

            return data.ToArray();
        }

        public async Task<bool> ApplyAssetEditAsync(
            string assetId,
            string newName,
            string? sourceImagePath,
            AssetImageCrop? crop,
            bool removeImage)
        {
            ThrowIfProtectedSystemAsset(assetId);
            var asset = configuration.Assets.FirstOrDefault(a =>
                a.Id == assetId &&
                (!a.IsSystem || a.Id == SubscribeSystemAssetId));
            if (asset == null)
            {
                throw new InvalidOperationException($"Asset not found: {assetId}");
            }

            var normalizedName = newName?.Trim() ?? string.Empty;
            var nameChanged =
                !asset.IsSystem &&
                !string.Equals(
                    asset.Name,
                    normalizedName,
                    StringComparison.CurrentCultureIgnoreCase);
            if (nameChanged)
            {
                if (string.IsNullOrWhiteSpace(normalizedName))
                {
                    throw new ArgumentException(
                        "Asset name cannot be empty.",
                        nameof(newName));
                }

                if (configuration.Assets.Any(other =>
                        other.Id != asset.Id &&
                        string.Equals(
                            other.Name,
                            normalizedName,
                            StringComparison.CurrentCultureIgnoreCase)))
                {
                    throw new InvalidOperationException(
                        $"Asset name already exists: {normalizedName}");
                }
            }

            var replacementImageBytes =
                !removeImage && !string.IsNullOrWhiteSpace(sourceImagePath)
                    ? CreateAssetImagePngFromFile(sourceImagePath, crop)
                    : null;
            var previousImageReference = asset.ImagePath;
            var previousImagePath = ResolveAssetImagePath(asset);
            var imageChanged =
                replacementImageBytes != null ||
                (removeImage &&
                 (!string.IsNullOrWhiteSpace(previousImageReference) ||
                  (!string.IsNullOrWhiteSpace(previousImagePath) &&
                   File.Exists(previousImagePath))));
            if (!nameChanged && !imageChanged)
            {
                return false;
            }

            string? replacementImagePath = null;
            if (replacementImageBytes != null)
            {
                replacementImagePath = Path.Combine(
                    GetAssetImageDirectory(),
                    $"{asset.Id}.png");
                ValidatePath(replacementImagePath, "assetImagePath");
            }

            var fileSnapshots =
                new Dictionary<string, (bool Exists, byte[]? Bytes, FileAttributes Attributes)>(
                    StringComparer.OrdinalIgnoreCase);
            foreach (var path in new[] { previousImagePath, replacementImagePath }
                         .Where(path => !string.IsNullOrWhiteSpace(path))
                         .Cast<string>()
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var exists = File.Exists(path);
                fileSnapshots[path] = exists
                    ? (true, File.ReadAllBytes(path), File.GetAttributes(path))
                    : (false, null, FileAttributes.Normal);
            }

            var previousName = asset.Name;
            UndoAction? undoAction = null;
            try
            {
                if (nameChanged)
                {
                    asset.Name = normalizedName;
                }

                if (replacementImageBytes != null)
                {
                    File.WriteAllBytes(replacementImagePath!, replacementImageBytes);
                    asset.ImagePath = Path.Combine(
                        AssetImageDirectoryName,
                        $"{asset.Id}.png");
                }
                else if (imageChanged)
                {
                    if (!string.IsNullOrWhiteSpace(previousImagePath) &&
                        File.Exists(previousImagePath))
                    {
                        File.SetAttributes(previousImagePath, FileAttributes.Normal);
                        File.Delete(previousImagePath);
                    }
                    asset.ImagePath = null;
                }

                var previousImageBytes =
                    !string.IsNullOrWhiteSpace(previousImagePath) &&
                    fileSnapshots.TryGetValue(previousImagePath, out var previousSnapshot) &&
                    previousSnapshot.Exists
                        ? previousSnapshot.Bytes
                        : null;
                undoAction = new UndoAction(
                    UndoActionType.AssetEdited,
                    $"Edited asset '{previousName}'")
                {
                    AssetId = asset.Id,
                    AssetName = asset.Name,
                    PreviousAssetName = previousName,
                    PreviousImagePath = previousImageReference,
                    PreviousImageBytes = previousImageBytes,
                    AssetNameChanged = nameChanged,
                    AssetImageChanged = imageChanged
                };
                undoManager.RecordAction(undoAction);
                await SaveConfigurationImmediatelyAsync();
                return true;
            }
            catch
            {
                asset.Name = previousName;
                asset.ImagePath = previousImageReference;
                foreach (var snapshot in fileSnapshots)
                {
                    if (!snapshot.Value.Exists)
                    {
                        if (File.Exists(snapshot.Key))
                        {
                            File.SetAttributes(snapshot.Key, FileAttributes.Normal);
                            File.Delete(snapshot.Key);
                        }
                        continue;
                    }

                    var directory = Path.GetDirectoryName(snapshot.Key);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    File.WriteAllBytes(snapshot.Key, snapshot.Value.Bytes!);
                    File.SetAttributes(snapshot.Key, snapshot.Value.Attributes);
                }

                if (undoAction != null &&
                    ReferenceEquals(undoManager.PeekLastAction(), undoAction))
                {
                    undoManager.PopLastAction();
                }
                throw;
            }
        }

        public void RemoveAssetImage(string assetId)
        {
            ThrowIfProtectedSystemAsset(assetId);
            var asset = configuration.Assets.FirstOrDefault(a =>
                a.Id == assetId && (!a.IsSystem || a.Id == SubscribeSystemAssetId));
            if (asset == null)
            {
                throw new InvalidOperationException($"Asset not found or is system asset: {assetId}");
            }

            var existingPath = ResolveAssetImagePath(asset);
            byte[]? previousBytes = null;
            if (!string.IsNullOrWhiteSpace(existingPath) && File.Exists(existingPath))
            {
                previousBytes = File.ReadAllBytes(existingPath);
                File.SetAttributes(existingPath, FileAttributes.Normal);
                File.Delete(existingPath);
            }

            var previousPath = asset.ImagePath;
            asset.ImagePath = null;
            undoManager.RecordAction(new UndoAction(
                UndoActionType.AssetImageChanged,
                $"Removed image from asset '{asset.Name}'")
            {
                AssetId = asset.Id,
                AssetName = asset.Name,
                PreviousImagePath = previousPath,
                PreviousImageBytes = previousBytes
            });
        }

        private string GetAssetImageDirectory()
        {
            EnsureDataDirectory();
            var imageDir = Path.Combine(managerPath, AssetImageDirectoryName);
            ValidatePath(imageDir, "assetImageDirectory");
            if (!Directory.Exists(imageDir))
            {
                Directory.CreateDirectory(imageDir);
            }
            return imageDir;
        }

        private static SKBitmap? DecodeBitmapFromFile(string path)
        {
            using var stream = File.OpenRead(path);
            using var codec = SKCodec.Create(stream);
            if (codec == null)
            {
                return null;
            }

            var info = codec.Info;
            var bitmap = new SKBitmap(info.Width, info.Height, info.ColorType, info.AlphaType);
            var result = codec.GetPixels(info, bitmap.GetPixels());
            if (result == SKCodecResult.Success || result == SKCodecResult.IncompleteInput)
            {
                return bitmap;
            }

            bitmap.Dispose();
            return null;
        }

        private static SKRectI NormalizeCropRect(AssetImageCrop? crop, int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                return new SKRectI(0, 0, Math.Max(1, width), Math.Max(1, height));
            }

            var xNorm = crop?.X ?? 0;
            var yNorm = crop?.Y ?? 0;
            var wNorm = crop?.Width ?? 1;
            var hNorm = crop?.Height ?? 1;

            xNorm = Math.Clamp(xNorm, 0, 1);
            yNorm = Math.Clamp(yNorm, 0, 1);
            wNorm = Math.Clamp(wNorm, 0, 1);
            hNorm = Math.Clamp(hNorm, 0, 1);

            var x = (int)Math.Round(xNorm * width);
            var y = (int)Math.Round(yNorm * height);
            var w = (int)Math.Round(wNorm * width);
            var h = (int)Math.Round(hNorm * height);

            if (w <= 0 || h <= 0)
            {
                x = 0;
                y = 0;
                w = width;
                h = height;
            }

            if (x < 0) x = 0;
            if (y < 0) y = 0;
            if (x + w > width) w = width - x;
            if (y + h > height) h = height - y;

            if (w <= 0 || h <= 0)
            {
                x = 0;
                y = 0;
                w = width;
                h = height;
            }

            return new SKRectI(x, y, x + w, y + h);
        }

        public void CreateAsset(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Asset name cannot be empty.", nameof(name));
            }

            name = name.Trim();
            if (AssetNameExists(name))
            {
                throw new InvalidOperationException($"Asset name already exists: {name}");
            }

            errorHandler.HandleInfo($"CreateAsset: Creating asset with name: {name}", "CreateAsset");
            var asset = new Asset(name);
            configuration.Assets.Add(asset);
            errorHandler.HandleInfo($"CreateAsset: Asset created with ID: {asset.Id}, Total assets: {configuration.Assets.Count}", "CreateAsset");
            
            // Undo險倬鹸
            undoManager.RecordAction(new UndoAction(UndoActionType.AssetCreated, $"Asset '{name}' created")
            {
                AssetId = asset.Id,
                AssetName = name
            });
        }

        public async Task<Asset> CreateAssetAsync(string name)
        {
            var previousIds = new HashSet<string>(
                configuration.Assets.Select(asset => asset.Id),
                StringComparer.Ordinal);
            CreateAsset(name);
            var created = configuration.Assets
                .Single(asset => !previousIds.Contains(asset.Id));
            var undoAction = undoManager.PeekLastAction();

            try
            {
                await SaveConfigurationImmediatelyAsync();
                return created;
            }
            catch
            {
                configuration.Assets.Remove(created);
                undoManager.RemoveAction(undoAction);
                throw;
            }
        }

        public void DeleteAsset(string assetId)
        {
            if (GmodDisabledAddonReconciliationService.IsProtectedSystemAsset(assetId))
            {
                return;
            }

            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId && !a.IsSystem);
            if (asset != null)
            {
                // Undo險倬鹸
                undoManager.RecordAction(new UndoAction(UndoActionType.AssetDeleted, $"Asset '{asset.Name}' deleted")
                {
                    AssetId = assetId,
                    AssetName = asset.Name,
                    DeletedAsset = asset,
                    AffectedAddonIds = new List<string>(asset.Addons),
                    PreviousAddonStates = new Dictionary<string, AddonState>(asset.AddonStates)
                });
                
                configuration.Assets.Remove(asset);
            }
        }

        public async Task<bool> DeleteAssetAsync(string assetId)
        {
            if (GmodDisabledAddonReconciliationService.IsProtectedSystemAsset(assetId))
            {
                return false;
            }

            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId && !a.IsSystem);
            if (asset == null)
            {
                return false;
            }

            var assetIndex = configuration.Assets.IndexOf(asset);
            DeleteAsset(assetId);
            var undoAction = undoManager.PeekLastAction();
            try
            {
                await SaveConfigurationImmediatelyAsync();
            }
            catch
            {
                if (configuration.Assets.All(current => current.Id != asset.Id))
                {
                    configuration.Assets.Insert(
                        Math.Min(assetIndex, configuration.Assets.Count),
                        asset);
                }
                undoManager.RemoveAction(undoAction);
                throw;
            }

            try
            {
                await UpdateAddonStatesAsync();
            }
            catch (Exception ex)
            {
                QueueRuntimeApplyProvider?.Invoke();
                errorHandler.HandleError(
                    ex,
                    "Asset deletion was saved; runtime reconciliation was queued",
                    ErrorSeverity.Warning);
            }
            return true;
        }

        public async Task SetAllOffAsync()
        {
            var previousStates = configuration.Assets.ToDictionary(
                asset => asset.Id,
                asset => asset.GetWholeState(),
                StringComparer.Ordinal);
            var changed = false;

            foreach (var asset in configuration.Assets)
            {
                if (asset.Id == SubscribeSystemAssetId)
                {
                    if (asset.GetWholeState() != AddonState.Disabled)
                    {
                        asset.SetWholeState(AddonState.Disabled);
                        changed = true;
                    }
                    continue;
                }

                if (!asset.IsSystem && asset.GetWholeState() == AddonState.Enabled)
                {
                    asset.SetWholeState(AddonState.Disabled);
                    changed = true;
                }
            }

            if (!changed)
            {
                return;
            }

            var undoAction = new UndoAction(
                UndoActionType.AllOff,
                "Turned all addon sources off")
            {
                PreviousAssetStates = previousStates
            };
            undoManager.RecordAction(undoAction);

            try
            {
                await SaveConfigurationImmediatelyAsync();
            }
            catch
            {
                foreach (var previous in previousStates)
                {
                    configuration.Assets
                        .FirstOrDefault(asset => asset.Id == previous.Key)
                        ?.SetWholeState(previous.Value);
                }
                undoManager.RemoveAction(undoAction);
                throw;
            }

            try
            {
                await UpdateAddonStatesAsync();
            }
            catch (Exception ex)
            {
                QueueRuntimeApplyProvider?.Invoke();
                errorHandler.HandleError(
                    ex,
                    "All-off was saved; runtime reconciliation was queued",
                    ErrorSeverity.Warning);
            }
        }

        public async Task<AssetVersion> CreateAssetVersionAsync(string assetId, string? note = null)
        {
            ThrowIfProtectedSystemAsset(assetId);
            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId && !a.IsSystem);
            if (asset == null)
            {
                throw new InvalidOperationException($"Custom asset not found: {assetId}");
            }

            var previousCurrentVersion = asset.CurrentVersion;
            var snapshot = assetVersionService.CreateSnapshot(asset, note);
            try
            {
                await SaveConfigurationImmediatelyAsync();
            }
            catch
            {
                asset.VersionHistory.Remove(snapshot);
                asset.CurrentVersion = previousCurrentVersion;
                throw;
            }
            return snapshot;
        }

        public async Task<bool> RestoreAssetVersionAsync(string assetId, int version)
        {
            if (GmodDisabledAddonReconciliationService.IsProtectedSystemAsset(assetId))
            {
                return false;
            }

            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId && !a.IsSystem);
            if (asset == null)
            {
                return false;
            }

            var previousMembership = asset.Addons.ToList();
            var previousCurrentVersion = asset.CurrentVersion;
            if (!assetVersionService.RestoreSnapshot(asset, version))
            {
                return false;
            }

            var undoAction = new UndoAction(
                UndoActionType.AssetVersionRestored,
                $"Restored version {version} of asset '{asset.Name}'")
            {
                AssetId = asset.Id,
                AssetName = asset.Name,
                PreviousMembership = previousMembership,
                PreviousCurrentVersion = previousCurrentVersion
            };
            undoManager.RecordAction(undoAction);

            try
            {
                await SaveConfigurationImmediatelyAsync();
            }
            catch
            {
                asset.Addons = previousMembership;
                asset.CurrentVersion = previousCurrentVersion;
                undoManager.RemoveAction(undoAction);
                throw;
            }

            try
            {
                await UpdateAddonStatesAsync();
            }
            catch (Exception ex)
            {
                QueueRuntimeApplyProvider?.Invoke();
                errorHandler.HandleError(
                    ex,
                    "Version restore was saved; runtime reconciliation was queued",
                    ErrorSeverity.Warning);
            }
            return true;
        }

        public async Task<bool> DeleteAssetVersionAsync(string assetId, int version)
        {
            if (GmodDisabledAddonReconciliationService.IsProtectedSystemAsset(assetId))
            {
                return false;
            }

            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId && !a.IsSystem);
            var snapshot = asset?.VersionHistory
                .FirstOrDefault(item => item.Version == version);
            if (asset == null || snapshot == null)
            {
                return false;
            }

            var snapshotIndex = asset.VersionHistory.IndexOf(snapshot);
            var previousCurrentVersion = asset.CurrentVersion;
            if (!assetVersionService.DeleteSnapshot(asset, version))
            {
                return false;
            }

            try
            {
                await SaveConfigurationImmediatelyAsync();
            }
            catch
            {
                asset.VersionHistory.Insert(
                    Math.Min(snapshotIndex, asset.VersionHistory.Count),
                    snapshot);
                asset.CurrentVersion = previousCurrentVersion;
                throw;
            }
            return true;
        }

        public async Task<int> ClearAssetVersionHistoryAsync(string assetId)
        {
            if (GmodDisabledAddonReconciliationService.IsProtectedSystemAsset(assetId))
            {
                return 0;
            }

            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId && !a.IsSystem);
            if (asset == null)
            {
                return 0;
            }

            var previousHistory = asset.VersionHistory.ToList();
            var previousCurrentVersion = asset.CurrentVersion;
            var removed = assetVersionService.ClearHistory(asset);
            if (removed > 0)
            {
                try
                {
                    await SaveConfigurationImmediatelyAsync();
                }
                catch
                {
                    asset.VersionHistory = previousHistory;
                    asset.CurrentVersion = previousCurrentVersion;
                    throw;
                }
            }
            return removed;
        }

        public bool AssetVersionHasMembershipChanges(string assetId, int version)
        {
            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId && !a.IsSystem);
            var snapshot = asset?.VersionHistory.FirstOrDefault(item => item.Version == version);
            return asset != null &&
                   snapshot != null &&
                   assetVersionService.HasMembershipChanges(asset, snapshot);
        }

        public void AddAddonToAsset(string assetId, string addonId, AddonState state = AddonState.Enabled)
        {
            if (GmodDisabledAddonReconciliationService.IsProtectedSystemAsset(assetId))
            {
                return;
            }

            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId);
            var normalizedAddonId = addonId?.Trim();
            if (asset != null &&
                !asset.ContainsAllAddons() &&
                !string.IsNullOrWhiteSpace(normalizedAddonId) &&
                !asset.Addons.Contains(normalizedAddonId))
            {
                addonId = normalizedAddonId;
                var operationId = eventLogger.NewOperationId();
                var beforeSnapshot = CaptureState();
                var beforeHash = ComputeStateHash(beforeSnapshot);
                var stopwatch = Stopwatch.StartNew();

                // Undo險倬鹸
                var addonInfo = configuration.AddonMetadata.ContainsKey(addonId)
                    ? configuration.AddonMetadata[addonId]
                    : null;
                var addonName = addonInfo?.Title ?? addonId;
                
                undoManager.RecordAction(new UndoAction(
                    UndoActionType.AddonAddedToAsset,
                    $"Added '{addonName}' to asset '{asset.Name}'")
                {
                    AssetId = assetId,
                    AssetName = asset.Name,
                    AddonId = addonId,
                    AddonName = addonName,
                    AffectedAddonIds = new List<string> { addonId },
                    AddonState = state
                });
                
                // Initialize WorkshopIconResolver
                if (assetId == "junction-system-asset")
                {
                    // Initialize WorkshopIconResolver
                    var sourceAssets = new List<string>();
                    foreach (var sourceAsset in configuration.Assets)
                    {
                        if (sourceAsset.Id != "junction-system-asset" && 
                            (sourceAsset.Addons.Contains(addonId) || sourceAsset.ContainsAllAddons()))
                        {
                            sourceAssets.Add(sourceAsset.Id);
                        }
                    }
                    
                    if (sourceAssets.Count > 0)
                    {
                        configuration.JunctionHistory[addonId] = sourceAssets;
                    }
                    
                    // Initialize WorkshopIconResolver
                    foreach (var otherAsset in configuration.Assets)
                    {
                        if (otherAsset.Id != "junction-system-asset")
                        {
                            if (otherAsset.ContainsAllAddons())
                            {
                                // Initialize WorkshopIconResolver
                                otherAsset.AddonStates[addonId] = AddonState.Excluded;
                            }
                            else
                            {
                                otherAsset.RemoveAddon(addonId);
                            }
                        }
                    }
                }
                
                try
                {
                    asset.AddAddon(addonId, state);
                    UpdateAddonStates();

                    stopwatch.Stop();
                    var afterSnapshot = CaptureState();
                    var afterHash = ComputeStateHash(afterSnapshot);

                    LogExperimentEvent(
                        "AssetAddAddon",
                        eventScope: "user",
                        targetId: addonId,
                        result: "success",
                        durationMs: stopwatch.ElapsedMilliseconds,
                        beforeHash: beforeHash,
                        afterHash: afterHash,
                        operationId: operationId,
                        assetId: assetId);
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    var afterSnapshot = CaptureState();
                    var afterHash = ComputeStateHash(afterSnapshot);
                    var errorCode = FindStrictLinkModeException(ex) != null
                        ? "strict_link_copy_blocked"
                        : "asset_add_failed";

                    LogExperimentEvent(
                        "AssetAddAddon",
                        eventScope: "user",
                        targetId: addonId,
                        result: "fail",
                        durationMs: stopwatch.ElapsedMilliseconds,
                        beforeHash: beforeHash,
                        afterHash: afterHash,
                        errorCode: errorCode,
                        operationId: operationId,
                        assetId: assetId);

                    throw;
                }
            }
        }

        // Initialize WorkshopIconResolver
        public void AddAddonsToAssetBatch(string assetId, List<string> addonIds, AddonState state = AddonState.Enabled, IProgress<(int current, int total)>? progress = null)
        {
            if (GmodDisabledAddonReconciliationService.IsProtectedSystemAsset(assetId))
            {
                return;
            }

            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId);
            if (asset == null ||
                asset.ContainsAllAddons() ||
                addonIds == null ||
                addonIds.Count == 0)
            {
                return;
            }

            var addedAddonIds = addonIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .Where(id => !asset.Addons.Contains(id))
                .ToList();
            if (addedAddonIds.Count == 0)
            {
                return;
            }

            // Record only IDs whose membership actually changes. Otherwise Undo
            // could remove a member that existed before this operation.
            var undoAction = new UndoAction(
                UndoActionType.AddonAddedToAsset,
                $"Added {addedAddonIds.Count} addons to asset '{asset.Name}'")
            {
                AssetId = assetId,
                AssetName = asset.Name,
                AffectedAddonIds = new List<string>(addedAddonIds),
                AddonState = state
            };
            undoManager.RecordAction(undoAction);

            // Undo記録
            var total = addedAddonIds.Count;
            progress?.Report((0, total));

            var current = 0;
            foreach (var id in addedAddonIds)
            {
                asset.AddAddon(id, state);
                current++;
                progress?.Report((current, total));
            }

            try
            {
                SaveConfigurationImmediatelySynchronously();
            }
            catch
            {
                foreach (var addonId in addedAddonIds)
                {
                    asset.RemoveAddon(addonId);
                }
                undoManager.RemoveAction(undoAction);
                throw;
            }

            try
            {
                UpdateAddonStates();
            }
            catch (Exception ex)
            {
                QueueRuntimeApplyProvider?.Invoke();
                errorHandler.HandleError(
                    ex,
                    "Asset membership was saved; runtime reconciliation was queued",
                    ErrorSeverity.Warning);
            }
        }

        /// <summary>
        /// Completes an Asset-created workflow without recording a second Undo unit.
        /// Undoing the existing AssetCreated action removes the new Asset together
        /// with every member added by this batch.
        /// </summary>
        public void AddAddonsToNewAssetBatch(
            string assetId,
            List<string> addonIds,
            AddonState state = AddonState.Enabled,
            IProgress<(int current, int total)>? progress = null)
        {
            var latestAction = undoManager.PeekLastAction();
            var canFoldIntoCreation =
                latestAction?.Type == UndoActionType.AssetCreated &&
                string.Equals(
                    latestAction.AssetId,
                    assetId,
                    StringComparison.Ordinal);
            if (!canFoldIntoCreation)
            {
                AddAddonsToAssetBatch(assetId, addonIds, state, progress);
                return;
            }

            using (undoManager.SuppressRecording())
            {
                AddAddonsToAssetBatch(assetId, addonIds, state, progress);
            }
        }

        public void RemoveAddonFromAsset(string assetId, string addonId)
        {
            RemoveAddonsFromAssetBatch(assetId, new List<string> { addonId });
        }

        // Batch removal to avoid per-addon full state updates
        public void RemoveAddonsFromAssetBatch(string assetId, List<string> addonIds, IProgress<(int current, int total)>? progress = null)
        {
            if (GmodDisabledAddonReconciliationService.IsProtectedSystemAsset(assetId))
            {
                return;
            }

            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId);
            if (asset == null || asset.ContainsAllAddons() || addonIds == null || addonIds.Count == 0)
            {
                return;
            }

            var normalizedAddonIds = addonIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (normalizedAddonIds.Count == 0)
            {
                return;
            }

            var operationId = eventLogger.NewOperationId();
            var beforeSnapshot = CaptureState();
            var beforeHash = ComputeStateHash(beforeSnapshot);
            var stopwatch = Stopwatch.StartNew();

            var previousStates = new Dictionary<string, AddonState>(StringComparer.Ordinal);
            var removedAddonIds = new List<string>();
            var removedAddonIdSet = new HashSet<string>(StringComparer.Ordinal);

            foreach (var addonId in normalizedAddonIds)
            {
                if (!asset.Addons.Contains(addonId) && !asset.AddonStates.ContainsKey(addonId))
                {
                    continue;
                }

                var currentState = asset.GetAddonState(addonId);
                previousStates[addonId] = currentState;
                removedAddonIds.Add(addonId);
                removedAddonIdSet.Add(addonId);
            }

            if (removedAddonIds.Count == 0)
            {
                return;
            }

            string description;
            string? addonName = null;
            string? singleAddonId = null;
            AddonState? singleAddonState = null;

            if (removedAddonIds.Count == 1)
            {
                singleAddonId = removedAddonIds[0];
                var addonInfo = configuration.AddonMetadata.ContainsKey(singleAddonId)
                    ? configuration.AddonMetadata[singleAddonId]
                    : null;
                addonName = addonInfo?.Title ?? singleAddonId;
                singleAddonState = previousStates.TryGetValue(singleAddonId, out var state) ? state : null;
                description = $"Removed '{addonName}' from asset '{asset.Name}'";
            }
            else
            {
                description = $"Removed {removedAddonIds.Count} addons from asset '{asset.Name}'";
            }

            var originalAddons = asset.Addons.ToList();
            var originalAddonStates =
                new Dictionary<string, AddonState>(asset.AddonStates);
            var undoAction = new UndoAction(
                UndoActionType.AddonRemovedFromAsset,
                description)
            {
                AssetId = assetId,
                AssetName = asset.Name,
                AddonId = singleAddonId,
                AddonName = addonName,
                AddonState = singleAddonState,
                AffectedAddonIds = removedAddonIds,
                PreviousAddonStates = previousStates
            };
            undoManager.RecordAction(undoAction);

            var total = normalizedAddonIds.Count;
            progress?.Report((0, total));
            var current = 0;

            try
            {
                foreach (var addonId in normalizedAddonIds)
                {
                    if (removedAddonIdSet.Contains(addonId))
                    {
                        asset.RemoveAddon(addonId);
                    }

                    current++;
                    progress?.Report((current, total));
                }

                try
                {
                    SaveConfigurationImmediatelySynchronously();
                }
                catch
                {
                    asset.Addons = originalAddons;
                    asset.AddonStates = originalAddonStates;
                    undoManager.RemoveAction(undoAction);
                    throw;
                }

                try
                {
                    UpdateAddonStates();
                }
                catch (Exception ex)
                {
                    QueueRuntimeApplyProvider?.Invoke();
                    errorHandler.HandleError(
                        ex,
                        "Asset membership was saved; runtime reconciliation was queued",
                        ErrorSeverity.Warning);
                }

                stopwatch.Stop();
                var afterSnapshot = CaptureState();
                var afterHash = ComputeStateHash(afterSnapshot);

                LogExperimentEvent(
                    "AssetRemoveAddon",
                    eventScope: "user",
                    targetId: singleAddonId ?? "batch",
                    result: "success",
                    durationMs: stopwatch.ElapsedMilliseconds,
                    beforeHash: beforeHash,
                    afterHash: afterHash,
                    operationId: operationId,
                    assetId: assetId,
                    note: removedAddonIds.Count > 1 ? $"batch_count={removedAddonIds.Count}" : null);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                var afterSnapshot = CaptureState();
                var afterHash = ComputeStateHash(afterSnapshot);
                var errorCode = FindStrictLinkModeException(ex) != null
                    ? "strict_link_copy_blocked"
                    : "asset_remove_failed";

                LogExperimentEvent(
                    "AssetRemoveAddon",
                    eventScope: "user",
                    targetId: singleAddonId ?? "batch",
                    result: "fail",
                    durationMs: stopwatch.ElapsedMilliseconds,
                    beforeHash: beforeHash,
                    afterHash: afterHash,
                    errorCode: errorCode,
                    operationId: operationId,
                    assetId: assetId,
                    note: removedAddonIds.Count > 1 ? $"batch_count={removedAddonIds.Count}" : null);

                throw;
            }
        }

        public async Task EnableAssetAsync(string assetId, IProgress<(int current, int total)>? progress = null)
        {
            if (GmodDisabledAddonReconciliationService.IsProtectedSystemAsset(assetId))
            {
                return;
            }

            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId);
            if (asset == null) return;

            // Initialize WorkshopIconResolver
            var addonIds = asset.ContainsAllAddons()
                ? configuration.AddonMetadata.Keys.Where(id => id != "*").ToList()
                : asset.Addons.Where(id => id != "*").ToList();

            // Undo險倬鹸
            var previousAddonStates = new Dictionary<string, AddonState>();
            foreach (var addonId in addonIds)
            {
                previousAddonStates[addonId] = asset.GetAddonState(addonId);
            }
            var undoAction = new UndoAction(UndoActionType.AssetEnabled, $"Enabled asset '{asset.Name}'")
            {
                AssetId = assetId,
                AssetName = asset.Name,
                PreviousEnabledState = asset.Enabled,
                PreviousDefaultAddonState = asset.DefaultAddonState,
                PreviousAddonStates = previousAddonStates,
                AffectedAddonIds = addonIds,
                IsAssetToggle = true,
                NewAddonState = AddonState.Enabled
            };
            undoManager.RecordAction(undoAction);

            asset.Enabled = true;
            
            foreach (var addonId in addonIds)
            {
                // Initialize WorkshopIconResolver
                if (asset.GetAddonState(addonId) != AddonState.Excluded)
                {
                    asset.SetAddonState(addonId, AddonState.Enabled);
                }
            }
            
            // Initialize WorkshopIconResolver
            try
            {
                using var suppressRecording = undoManager.SuppressRecording();
                await UpdateAddonStatesAsync(progress);
            }
            finally
            {
                undoManager.MoveToTop(undoAction);
            }
        }

        public async Task DisableAssetAsync(string assetId, IProgress<(int current, int total)>? progress = null)
        {
            if (GmodDisabledAddonReconciliationService.IsProtectedSystemAsset(assetId))
            {
                return;
            }

            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId);
            if (asset == null) return;

            // Initialize WorkshopIconResolver
            var addonIds = asset.ContainsAllAddons()
                ? configuration.AddonMetadata.Keys.Where(id => id != "*").ToList()
                : asset.Addons.Where(id => id != "*").ToList();

            // Undo險倬鹸
            var previousAddonStates = new Dictionary<string, AddonState>();
            foreach (var addonId in addonIds)
            {
                previousAddonStates[addonId] = asset.GetAddonState(addonId);
            }
            var undoAction = new UndoAction(UndoActionType.AssetDisabled, $"Disabled asset '{asset.Name}'")
            {
                AssetId = assetId,
                AssetName = asset.Name,
                PreviousEnabledState = asset.Enabled,
                PreviousDefaultAddonState = asset.DefaultAddonState,
                PreviousAddonStates = previousAddonStates,
                AffectedAddonIds = addonIds,
                IsAssetToggle = true,
                NewAddonState = AddonState.Disabled
            };
            undoManager.RecordAction(undoAction);

            asset.Enabled = true;
            
            foreach (var addonId in addonIds)
            {
                // Initialize WorkshopIconResolver
                if (asset.GetAddonState(addonId) != AddonState.Excluded)
                {
                    asset.SetAddonState(addonId, AddonState.Disabled);
                }
            }
            
            // Initialize WorkshopIconResolver
            try
            {
                using var suppressRecording = undoManager.SuppressRecording();
                await UpdateAddonStatesAsync(progress);
            }
            finally
            {
                undoManager.MoveToTop(undoAction);
            }
        }

        public async Task SetAssetEnabledAsync(
            string assetId,
            bool enabled,
            IProgress<(int current, int total)>? progress = null,
            bool updateAddonStates = true)
        {
            if (GmodDisabledAddonReconciliationService.IsProtectedSystemAsset(assetId))
            {
                return;
            }

            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId);
            if (asset == null || asset.Enabled == enabled)
            {
                return;
            }

            var undoAction = new UndoAction(
                enabled ? UndoActionType.AssetEnabled : UndoActionType.AssetDisabled,
                $"{(enabled ? "Activated" : "Deactivated")} asset '{asset.Name}'")
            {
                AssetId = assetId,
                AssetName = asset.Name,
                PreviousEnabledState = asset.Enabled,
                PreviousDefaultAddonState = asset.DefaultAddonState,
                IsAssetToggle = true,
                NewAddonState = asset.DefaultAddonState
            };
            undoManager.RecordAction(undoAction);

            asset.Enabled = enabled;

            if (!updateAddonStates)
            {
                return;
            }

            try
            {
                using var suppressRecording = undoManager.SuppressRecording();
                await UpdateAddonStatesAsync(progress);
            }
            finally
            {
                undoManager.MoveToTop(undoAction);
            }
        }

        public async Task ApplyAssetDefaultStateAsync(string assetId, AddonState newDefaultState, IProgress<(int current, int total)>? progress = null)
        {
            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId);
            if (asset == null) return;
            if (!Enum.IsDefined(typeof(AddonState), newDefaultState))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(newDefaultState),
                    newDefaultState,
                    "Unknown Asset state.");
            }
            if (asset.GetWholeState() == newDefaultState)
            {
                return;
            }

            var addonIds = asset.ContainsAllAddons()
                ? configuration.AddonMetadata.Keys.Where(id => id != "*").ToList()
                : asset.Addons.Where(id => id != "*").ToList();

            var previousAddonStates = new Dictionary<string, AddonState>();
            foreach (var addonId in addonIds)
            {
                previousAddonStates[addonId] = asset.GetAddonState(addonId);
            }

            var actionType = newDefaultState switch
            {
                AddonState.Enabled => UndoActionType.AssetEnabled,
                AddonState.Disabled => UndoActionType.AssetDisabled,
                AddonState.Excluded => UndoActionType.AssetExcluded,
                _ => UndoActionType.AssetEnabled
            };

            var description = newDefaultState switch
            {
                AddonState.Enabled => $"Enabled asset '{asset.Name}'",
                AddonState.Disabled => $"Disabled asset '{asset.Name}'",
                AddonState.Excluded => $"Excluded asset '{asset.Name}'",
                _ => $"Updated asset '{asset.Name}' state"
            };

            var undoAction = new UndoAction(actionType, description)
            {
                AssetId = assetId,
                AssetName = asset.Name,
                PreviousEnabledState = asset.Enabled,
                PreviousDefaultAddonState = asset.DefaultAddonState,
                PreviousAddonStates = previousAddonStates,
                AffectedAddonIds = addonIds,
                IsAssetToggle = false,
                NewAddonState = newDefaultState
            };
            undoManager.RecordAction(undoAction);

            asset.Enabled = true;
            asset.DefaultAddonState = newDefaultState;

            foreach (var addonId in addonIds)
            {
                asset.SetAddonState(addonId, newDefaultState);
            }

            try
            {
                await SaveConfigurationImmediatelyAsync();
            }
            catch
            {
                asset.SetWholeState(
                    undoAction.PreviousDefaultAddonState ?? AddonState.Disabled);
                undoManager.RemoveAction(undoAction);
                throw;
            }

            try
            {
                await UpdateAddonStatesAsync(progress);
            }
            catch (Exception ex)
            {
                QueueRuntimeApplyProvider?.Invoke();
                errorHandler.HandleError(
                    ex,
                    "Asset state was saved; runtime reconciliation was queued",
                    ErrorSeverity.Warning);
            }
        }

        public async Task<AssetApplyResult> ApplyAssetExclusiveAsync(string assetId, IProgress<(int current, int total)>? progress = null)
        {
            var result = new AssetApplyResult { AssetId = assetId };
            var operationId = eventLogger.NewOperationId();
            var beforeSnapshot = CaptureState();
            var beforeHash = ComputeStateHash(beforeSnapshot);
            var beforeHashScope = beforeSnapshot.Source;
            result.BeforeHash = beforeHash;

            var asset = configuration.Assets.FirstOrDefault(a =>
                a.Id == assetId &&
                !GmodDisabledAddonReconciliationService.IsProtectedSystemAsset(a.Id));
            var fromAssets = configuration.Assets
                .Where(a => !a.IsSystem && a.Enabled)
                .ToList();
            var fromAssetIds = fromAssets.Select(a => a.Id).ToList();
            var fromAssetLabels = fromAssets.Select(a => GetAssetLabelCode(a.Name)).ToList();
            var fromAssetDisplayNames = fromAssets.Select(a => a.Name).ToList();
            var toAssetLabel = asset != null ? GetAssetLabelCode(asset.Name) : null;
            var toAssetDisplayName = asset?.Name;

            LogExperimentEvent(
                "AssetApplyExclusiveStart",
                eventScope: "user",
                targetId: assetId,
                result: "start",
                beforeHash: beforeHash,
                operationId: operationId,
                assetId: assetId,
                assetLabel: toAssetLabel,
                assetDisplayName: toAssetDisplayName,
                fromAssetIds: fromAssetIds,
                fromAssetLabels: fromAssetLabels,
                fromAssetDisplayNames: fromAssetDisplayNames,
                toAssetId: assetId,
                toAssetLabel: toAssetLabel,
                toAssetDisplayName: toAssetDisplayName,
                stateHashScope: beforeHashScope);
            if (asset == null)
            {
                result.Success = false;
                result.ErrorCode = "asset_not_found";
                result.AfterHash = beforeHash;
                result.ExpectedHash = beforeHash;
                LogExperimentEvent(
                    "AssetApplyExclusiveEnd",
                    eventScope: "system",
                    targetId: assetId,
                    result: "fail",
                    durationMs: 0,
                    beforeHash: result.BeforeHash,
                    afterHash: result.AfterHash,
                    expectedHash: result.ExpectedHash,
                    errorCode: result.ErrorCode,
                    operationId: operationId,
                    assetId: assetId,
                    assetLabel: toAssetLabel,
                    assetDisplayName: toAssetDisplayName,
                    fromAssetIds: fromAssetIds,
                    fromAssetLabels: fromAssetLabels,
                    fromAssetDisplayNames: fromAssetDisplayNames,
                    toAssetId: assetId,
                    toAssetLabel: toAssetLabel,
                    toAssetDisplayName: toAssetDisplayName,
                    stateHashScope: beforeHashScope);
                return result;
            }

            var stopwatch = Stopwatch.StartNew();
            StateMatchResult? matchResult = null;
            StrictLinkModeException? strictFailure = null;
            string? errorCode = null;
            AddonStateSnapshot? afterSnapshot = null;
            AddonStateSnapshot? expectedSnapshot = null;

            try
            {
                foreach (var item in configuration.Assets)
                {
                    item.Enabled = item.Id == assetId;
                }

                asset.Enabled = true;

                matchResult = await UpdateAddonStatesInternalAsync(logEvents: true, parentOperationId: operationId, progress: progress);
                await SaveConfigurationAsync();

                result.Success = matchResult.Succeeded;
                afterSnapshot = matchResult.Snapshot;
                expectedSnapshot = matchResult.ExpectedSnapshot;
                result.AfterHash = ComputeStateHash(afterSnapshot);
                result.ExpectedHash = ComputeStateHash(expectedSnapshot);
                errorCode = matchResult.Succeeded
                    ? null
                    : matchResult.FailureCode ??
                      (matchResult.RuntimeWriteSucceeded
                          ? "state_mismatch"
                          : "runtime_write_failed");
            }
            catch (StrictLinkModeException ex)
            {
                strictFailure = ex;
                result.Success = false;
                errorCode = "strict_link_copy_blocked";
            }
            catch (Exception ex)
            {
                result.Success = false;
                errorCode = "apply_failed";
                errorHandler.HandleError(ex, $"ApplyAssetExclusive failed for asset {assetId}", ErrorSeverity.Warning);
            }
            finally
            {
                stopwatch.Stop();
                result.DurationMs = stopwatch.ElapsedMilliseconds;

                if (matchResult == null)
                {
                    afterSnapshot = CaptureState();
                    expectedSnapshot = CaptureExpectedStateSnapshot();
                    result.AfterHash = ComputeStateHash(afterSnapshot);
                    result.ExpectedHash = ComputeStateHash(expectedSnapshot);
                }

                LogExperimentEvent(
                    "AssetApplyExclusiveEnd",
                    eventScope: "system",
                    targetId: assetId,
                    result: result.Success ? "success" : "fail",
                    durationMs: result.DurationMs,
                    beforeHash: result.BeforeHash,
                    afterHash: result.AfterHash,
                    expectedHash: result.ExpectedHash,
                    errorCode: errorCode,
                    operationId: operationId,
                    metrics: matchResult?.Metrics,
                    assetId: assetId,
                    assetLabel: toAssetLabel,
                    assetDisplayName: toAssetDisplayName,
                    fromAssetIds: fromAssetIds,
                    fromAssetLabels: fromAssetLabels,
                    fromAssetDisplayNames: fromAssetDisplayNames,
                    toAssetId: assetId,
                    toAssetLabel: toAssetLabel,
                    toAssetDisplayName: toAssetDisplayName,
                    stateHashScope: afterSnapshot?.Source ?? beforeHashScope,
                    expectedHashScope: expectedSnapshot?.Source);
            }

            if (strictFailure != null)
            {
                throw strictFailure;
            }

            return result;
        }

        public string GetWorkshopPath() => workshopPath;
        public string GetManagerPath() => managerPath;
        
        public SteamWorkshopService GetSteamWorkshopService() => steamWorkshopService;
        
        public IIconResolver GetWorkshopIconResolver()
        {
            return workshopIconResolver;
        }
        
        public string GetThumbnailCachePath()
        {
            var path = Path.Combine(managerPath, "thumbnails");
            ValidatePath(path, "thumbnailCachePath");
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            return path;
        }
        
        /// <summary>
        // Initialize WorkshopIconResolver
        /// </summary>
        public async Task<bool> UpdateAddonStatesAsync(IProgress<(int current, int total)>? progress = null)
        {
            var result = await UpdateAddonStatesWithResultAsync(
                progress,
                allowConflictSupersede: true);
            return result.Succeeded;
        }

        internal async Task<RuntimeApplyAttemptResult> UpdateAddonStatesWithResultAsync(
            IProgress<(int current, int total)>? progress = null,
            bool allowConflictSupersede = false)
        {
            await _runtimeReconcileGate.WaitAsync();
            try
            {
                await SaveConfigurationImmediatelyAsync();

                if (DeferRuntimeApplyIfGmodRunning(allowConflictSupersede))
                {
                    return new RuntimeApplyAttemptResult(
                        succeeded: false,
                        failureCode: "gmod_running_deferred");
                }

                var result = await UpdateAddonStatesCoreAsync(
                    logEvents: true,
                    parentOperationId: null,
                    progress: progress,
                    observeExternalChanges: true,
                    allowConflictSupersede: allowConflictSupersede);
                if (!result.Succeeded)
                {
                    // A conflict is an intentional fail-closed stop. Re-queueing
                    // it would replace the generation PendingChangeManager must
                    // clear and could overwrite GMod on the next pass.
                    if (!string.Equals(
                            result.FailureCode,
                            RuntimeAttributionConflictFailureCode,
                            StringComparison.Ordinal))
                    {
                        QueueRuntimeApplyProvider?.Invoke();
                        if (allowConflictSupersede &&
                            !string.IsNullOrWhiteSpace(
                                result.SupersededConflictOperationId))
                        {
                            await FinalizeRuntimeAttributionConflictCoreAsync(
                                result.SupersededConflictOperationId);
                        }
                    }
                }

                // Empty target scopes and no-op writes do not replace the old
                // journal. A successful explicit operation may still safely
                // finalize that exact conflict while holding the runtime gate.
                if (result.Succeeded &&
                    allowConflictSupersede &&
                    !string.IsNullOrWhiteSpace(
                        result.SupersededConflictOperationId))
                {
                    await FinalizeRuntimeAttributionConflictCoreAsync(
                        result.SupersededConflictOperationId);
                }

                return new RuntimeApplyAttemptResult(
                    result.Succeeded,
                    result.FailureCode,
                    result.AttributionConflictOperationId);
            }
            finally
            {
                _runtimeReconcileGate.Release();
            }
        }

        internal sealed class RuntimeApplyAttemptResult
        {
            public RuntimeApplyAttemptResult(
                bool succeeded,
                string? failureCode,
                string? attributionConflictOperationId = null)
            {
                Succeeded = succeeded;
                FailureCode = failureCode;
                AttributionConflictOperationId = attributionConflictOperationId;
            }

            public bool Succeeded { get; }
            public string? FailureCode { get; }
            public string? AttributionConflictOperationId { get; }
        }

        private sealed class AssetResolution
        {
            public Asset? Asset { get; set; }
            public string? ResolvedId { get; set; }
            public string? LabelCode { get; set; }
            public string? DisplayName { get; set; }
            public string? ResolveMethod { get; set; }
            public string? ErrorCode { get; set; }

            public bool IsResolved => Asset != null;
        }

        private static string NormalizeAssetLabel(string label, out string? methodDetail)
        {
            var trimmed = label.Trim();
            methodDetail = null;

            const string assetPrefix = "asset ";
            if (trimmed.StartsWith(assetPrefix, StringComparison.OrdinalIgnoreCase))
            {
                methodDetail = "label_normalized:strip_prefix(Asset )";
                trimmed = trimmed.Substring(assetPrefix.Length).Trim();
            }

            return trimmed;
        }

        private static string GetAssetLabelCode(string label)
        {
            var normalized = NormalizeAssetLabel(label, out _);
            return normalized.Trim();
        }

        private AssetResolution ResolveAssetReference(string? assetId, string? assetLabel)
        {
            if (string.IsNullOrWhiteSpace(assetId) && string.IsNullOrWhiteSpace(assetLabel))
            {
                return new AssetResolution();
            }

            if (!string.IsNullOrWhiteSpace(assetId))
            {
                var matches = configuration.Assets
                    .Where(a => string.Equals(a.Id, assetId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (matches.Count == 1)
                {
                    var asset = matches[0];
                    return new AssetResolution
                    {
                        Asset = asset,
                        ResolvedId = asset.Id,
                        LabelCode = GetAssetLabelCode(asset.Name),
                        DisplayName = asset.Name,
                        ResolveMethod = "id"
                    };
                }

                return new AssetResolution
                {
                    ResolvedId = assetId.Trim(),
                    LabelCode = assetId.Trim(),
                    ResolveMethod = "id",
                    ErrorCode = matches.Count > 1 ? "asset_resolve_ambiguous:id" : "asset_not_found:id"
                };
            }

            var rawLabel = assetLabel?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(rawLabel))
            {
                return new AssetResolution { ErrorCode = "asset_label_missing" };
            }

            var matchesByName = configuration.Assets
                .Where(a => string.Equals(a.Name, rawLabel, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matchesByName.Count == 1)
            {
                var asset = matchesByName[0];
                return new AssetResolution
                {
                    Asset = asset,
                    ResolvedId = asset.Id,
                    LabelCode = GetAssetLabelCode(asset.Name),
                    DisplayName = asset.Name,
                    ResolveMethod = "label_exact"
                };
            }

            if (matchesByName.Count > 1)
            {
                return new AssetResolution
                {
                    LabelCode = GetAssetLabelCode(rawLabel),
                    ResolveMethod = "label_exact",
                    ErrorCode = "asset_resolve_ambiguous:label_exact"
                };
            }

            var matchesById = configuration.Assets
                .Where(a => string.Equals(a.Id, rawLabel, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matchesById.Count == 1)
            {
                var asset = matchesById[0];
                return new AssetResolution
                {
                    Asset = asset,
                    ResolvedId = asset.Id,
                    LabelCode = GetAssetLabelCode(asset.Name),
                    DisplayName = asset.Name,
                    ResolveMethod = "label_id"
                };
            }

            if (matchesById.Count > 1)
            {
                return new AssetResolution
                {
                    LabelCode = GetAssetLabelCode(rawLabel),
                    ResolveMethod = "label_id",
                    ErrorCode = "asset_resolve_ambiguous:label_id"
                };
            }

            var matchesByCode = configuration.Assets
                .Where(a => string.Equals(GetAssetLabelCode(a.Name), rawLabel, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matchesByCode.Count == 1)
            {
                var asset = matchesByCode[0];
                return new AssetResolution
                {
                    Asset = asset,
                    ResolvedId = asset.Id,
                    LabelCode = GetAssetLabelCode(asset.Name),
                    DisplayName = asset.Name,
                    ResolveMethod = "label_code"
                };
            }

            if (matchesByCode.Count > 1)
            {
                return new AssetResolution
                {
                    LabelCode = GetAssetLabelCode(rawLabel),
                    ResolveMethod = "label_code",
                    ErrorCode = "asset_resolve_ambiguous:label_code"
                };
            }

            var normalized = NormalizeAssetLabel(rawLabel, out var normalizeMethod);
            if (!string.Equals(normalized, rawLabel, StringComparison.Ordinal))
            {
                var matchesByNormalizedName = configuration.Assets
                    .Where(a => string.Equals(a.Name, normalized, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (matchesByNormalizedName.Count == 1)
                {
                    var asset = matchesByNormalizedName[0];
                    return new AssetResolution
                    {
                        Asset = asset,
                        ResolvedId = asset.Id,
                        LabelCode = GetAssetLabelCode(asset.Name),
                        DisplayName = asset.Name,
                        ResolveMethod = normalizeMethod ?? "label_normalized"
                    };
                }

                if (matchesByNormalizedName.Count > 1)
                {
                    return new AssetResolution
                    {
                        LabelCode = GetAssetLabelCode(normalized),
                        ResolveMethod = normalizeMethod ?? "label_normalized",
                        ErrorCode = "asset_resolve_ambiguous:label_normalized"
                    };
                }

                var matchesByNormalizedId = configuration.Assets
                    .Where(a => string.Equals(a.Id, normalized, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (matchesByNormalizedId.Count == 1)
                {
                    var asset = matchesByNormalizedId[0];
                    return new AssetResolution
                    {
                        Asset = asset,
                        ResolvedId = asset.Id,
                        LabelCode = GetAssetLabelCode(asset.Name),
                        DisplayName = asset.Name,
                        ResolveMethod = (normalizeMethod ?? "label_normalized") + ":id"
                    };
                }

                if (matchesByNormalizedId.Count > 1)
                {
                    return new AssetResolution
                    {
                        LabelCode = GetAssetLabelCode(normalized),
                        ResolveMethod = (normalizeMethod ?? "label_normalized") + ":id",
                        ErrorCode = "asset_resolve_ambiguous:label_normalized"
                    };
                }

                var matchesByNormalizedCode = configuration.Assets
                    .Where(a => string.Equals(GetAssetLabelCode(a.Name), normalized, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (matchesByNormalizedCode.Count == 1)
                {
                    var asset = matchesByNormalizedCode[0];
                    return new AssetResolution
                    {
                        Asset = asset,
                        ResolvedId = asset.Id,
                        LabelCode = GetAssetLabelCode(asset.Name),
                        DisplayName = asset.Name,
                        ResolveMethod = (normalizeMethod ?? "label_normalized") + ":code"
                    };
                }

                if (matchesByNormalizedCode.Count > 1)
                {
                    return new AssetResolution
                    {
                        LabelCode = GetAssetLabelCode(normalized),
                        ResolveMethod = (normalizeMethod ?? "label_normalized") + ":code",
                        ErrorCode = "asset_resolve_ambiguous:label_normalized"
                    };
                }

                return new AssetResolution
                {
                    LabelCode = GetAssetLabelCode(normalized),
                    ResolveMethod = normalizeMethod ?? "label_normalized",
                    ErrorCode = "asset_not_found:label_normalized"
                };
            }

            return new AssetResolution
            {
                LabelCode = GetAssetLabelCode(rawLabel),
                ResolveMethod = "label_exact",
                ErrorCode = "asset_not_found:label_exact"
            };
        }

        private static string? BuildAssetResolutionError(AssetResolution fromAsset, AssetResolution toAsset)
        {
            if (fromAsset.ErrorCode == null && toAsset.ErrorCode == null)
            {
                return null;
            }

            if (fromAsset.ErrorCode != null && toAsset.ErrorCode != null)
            {
                return $"from_{fromAsset.ErrorCode};to_{toAsset.ErrorCode}";
            }

            if (fromAsset.ErrorCode != null)
            {
                return $"from_{fromAsset.ErrorCode}";
            }

            return $"to_{toAsset.ErrorCode}";
        }

        public bool LogTaskStart(
            string taskId,
            out string? errorCode,
            string? note = null,
            string? fromAssetId = null,
            string? fromAssetLabel = null,
            string? toAssetId = null,
            string? toAssetLabel = null)
        {
            errorCode = null;
            if (string.IsNullOrWhiteSpace(taskId))
            {
                errorCode = "task_id_missing";
                return false;
            }

            var snapshot = CaptureState();
            var beforeHash = ComputeStateHash(snapshot);
            var fromAsset = ResolveAssetReference(fromAssetId, fromAssetLabel);
            var toAsset = ResolveAssetReference(toAssetId, toAssetLabel);
            errorCode = BuildAssetResolutionError(fromAsset, toAsset);

            var resolvedFromId = fromAsset.ResolvedId ?? fromAssetId;
            var resolvedFromLabel = fromAsset.LabelCode ?? (string.IsNullOrWhiteSpace(fromAssetLabel) ? null : GetAssetLabelCode(fromAssetLabel));
            var resolvedFromDisplay = fromAsset.DisplayName;
            var resolvedToId = toAsset.ResolvedId ?? toAssetId;
            var resolvedToLabel = toAsset.LabelCode ?? (string.IsNullOrWhiteSpace(toAssetLabel) ? null : GetAssetLabelCode(toAssetLabel));
            var resolvedToDisplay = toAsset.DisplayName;

            var fromAssetIds = string.IsNullOrWhiteSpace(resolvedFromId) ? null : new List<string> { resolvedFromId };
            var fromAssetLabels = string.IsNullOrWhiteSpace(resolvedFromLabel) ? null : new List<string> { resolvedFromLabel };
            var fromAssetDisplayNames = string.IsNullOrWhiteSpace(resolvedFromDisplay) ? null : new List<string> { resolvedFromDisplay };
            var expectedSnapshot = CaptureExpectedStateSnapshotForAsset(resolvedToId);
            var expectedHash = expectedSnapshot != null ? ComputeStateHash(expectedSnapshot) : null;

            LogExperimentEvent(
                "TaskStart",
                eventScope: "external",
                targetId: taskId,
                result: errorCode == null ? "start" : "fail",
                beforeHash: beforeHash,
                expectedHash: expectedHash,
                errorCode: errorCode,
                note: note,
                taskIdOverride: taskId,
                fromAssetIds: fromAssetIds,
                fromAssetLabels: fromAssetLabels,
                fromAssetDisplayNames: fromAssetDisplayNames,
                toAssetId: resolvedToId,
                toAssetLabel: resolvedToLabel,
                toAssetDisplayName: resolvedToDisplay,
                stateHashScope: snapshot.Source,
                expectedHashScope: expectedSnapshot?.Source,
                fromAssetResolveMethod: fromAsset.ResolveMethod,
                toAssetResolveMethod: toAsset.ResolveMethod);

            return errorCode == null;
        }

        public bool LogTaskEnd(
            string taskId,
            out string? errorCode,
            string? expectedHash = null,
            bool? taskSuccess = null,
            string? note = null,
            string? fromAssetId = null,
            string? fromAssetLabel = null,
            string? toAssetId = null,
            string? toAssetLabel = null)
        {
            errorCode = null;
            if (string.IsNullOrWhiteSpace(taskId))
            {
                errorCode = "task_id_missing";
                return false;
            }

            var snapshot = CaptureState();
            var finalHash = ComputeStateHash(snapshot);
            var success = taskSuccess;
            var fromAsset = ResolveAssetReference(fromAssetId, fromAssetLabel);
            var toAsset = ResolveAssetReference(toAssetId, toAssetLabel);
            errorCode = BuildAssetResolutionError(fromAsset, toAsset);

            var resolvedFromId = fromAsset.ResolvedId ?? fromAssetId;
            var resolvedFromLabel = fromAsset.LabelCode ?? (string.IsNullOrWhiteSpace(fromAssetLabel) ? null : GetAssetLabelCode(fromAssetLabel));
            var resolvedFromDisplay = fromAsset.DisplayName;
            var resolvedToId = toAsset.ResolvedId ?? toAssetId;
            var resolvedToLabel = toAsset.LabelCode ?? (string.IsNullOrWhiteSpace(toAssetLabel) ? null : GetAssetLabelCode(toAssetLabel));
            var resolvedToDisplay = toAsset.DisplayName;
            var fromAssetIds = string.IsNullOrWhiteSpace(resolvedFromId) ? null : new List<string> { resolvedFromId };
            var fromAssetLabels = string.IsNullOrWhiteSpace(resolvedFromLabel) ? null : new List<string> { resolvedFromLabel };
            var fromAssetDisplayNames = string.IsNullOrWhiteSpace(resolvedFromDisplay) ? null : new List<string> { resolvedFromDisplay };
            var resolvedExpectedHash = expectedHash;
            AddonStateSnapshot? expectedSnapshot = null;

            if (string.IsNullOrWhiteSpace(resolvedExpectedHash) && !string.IsNullOrWhiteSpace(resolvedToId))
            {
                expectedSnapshot = CaptureExpectedStateSnapshotForAsset(resolvedToId);
                resolvedExpectedHash = expectedSnapshot != null ? ComputeStateHash(expectedSnapshot) : null;
            }

            if (!string.IsNullOrWhiteSpace(resolvedExpectedHash) && !success.HasValue)
            {
                success = string.Equals(finalHash, resolvedExpectedHash, StringComparison.Ordinal);
            }

            var result = errorCode != null
                ? "fail"
                : (success.HasValue ? (success.Value ? "success" : "fail") : "end");

            LogExperimentEvent(
                "TaskEnd",
                eventScope: "external",
                targetId: taskId,
                result: result,
                afterHash: finalHash,
                expectedHash: resolvedExpectedHash,
                taskSuccess: success,
                finalHash: finalHash,
                errorCode: errorCode,
                note: note,
                taskIdOverride: taskId,
                fromAssetIds: fromAssetIds,
                fromAssetLabels: fromAssetLabels,
                fromAssetDisplayNames: fromAssetDisplayNames,
                toAssetId: resolvedToId,
                toAssetLabel: resolvedToLabel,
                toAssetDisplayName: resolvedToDisplay,
                stateHashScope: snapshot.Source,
                expectedHashScope: expectedSnapshot?.Source,
                fromAssetResolveMethod: fromAsset.ResolveMethod,
                toAssetResolveMethod: toAsset.ResolveMethod);

            return errorCode == null;
        }

        public bool LogBlSwitchStart(
            out string? errorCode,
            string? blMethod = null,
            string? note = null,
            string? fromAssetId = null,
            string? fromAssetLabel = null,
            string? toAssetId = null,
            string? toAssetLabel = null)
        {
            errorCode = null;
            var snapshot = CaptureState();
            var beforeHash = ComputeStateHash(snapshot);
            var fromAsset = ResolveAssetReference(fromAssetId, fromAssetLabel);
            var toAsset = ResolveAssetReference(toAssetId, toAssetLabel);
            errorCode = BuildAssetResolutionError(fromAsset, toAsset);

            var resolvedFromId = fromAsset.ResolvedId ?? fromAssetId;
            var resolvedFromLabel = fromAsset.LabelCode ?? (string.IsNullOrWhiteSpace(fromAssetLabel) ? null : GetAssetLabelCode(fromAssetLabel));
            var resolvedFromDisplay = fromAsset.DisplayName;
            var resolvedToId = toAsset.ResolvedId ?? toAssetId;
            var resolvedToLabel = toAsset.LabelCode ?? (string.IsNullOrWhiteSpace(toAssetLabel) ? null : GetAssetLabelCode(toAssetLabel));
            var resolvedToDisplay = toAsset.DisplayName;
            var fromAssetIds = string.IsNullOrWhiteSpace(resolvedFromId) ? null : new List<string> { resolvedFromId };
            var fromAssetLabels = string.IsNullOrWhiteSpace(resolvedFromLabel) ? null : new List<string> { resolvedFromLabel };
            var fromAssetDisplayNames = string.IsNullOrWhiteSpace(resolvedFromDisplay) ? null : new List<string> { resolvedFromDisplay };

            LogExperimentEvent(
                "BlSwitchStart",
                eventScope: "external",
                targetId: blMethod ?? "bl",
                result: errorCode == null ? "start" : "fail",
                beforeHash: beforeHash,
                blMethod: blMethod,
                note: note,
                errorCode: errorCode,
                fromAssetIds: fromAssetIds,
                fromAssetLabels: fromAssetLabels,
                fromAssetDisplayNames: fromAssetDisplayNames,
                toAssetId: resolvedToId,
                toAssetLabel: resolvedToLabel,
                toAssetDisplayName: resolvedToDisplay,
                stateHashScope: snapshot.Source,
                fromAssetResolveMethod: fromAsset.ResolveMethod,
                toAssetResolveMethod: toAsset.ResolveMethod);

            return errorCode == null;
        }

        public bool LogBlSwitchEnd(
            out string? errorCode,
            string? blMethod = null,
            string? expectedHash = null,
            bool? success = null,
            string? note = null,
            string? fromAssetId = null,
            string? fromAssetLabel = null,
            string? toAssetId = null,
            string? toAssetLabel = null)
        {
            errorCode = null;
            var snapshot = CaptureState();
            var finalHash = ComputeStateHash(snapshot);
            var resolved = success;
            var fromAsset = ResolveAssetReference(fromAssetId, fromAssetLabel);
            var toAsset = ResolveAssetReference(toAssetId, toAssetLabel);
            errorCode = BuildAssetResolutionError(fromAsset, toAsset);

            var resolvedFromId = fromAsset.ResolvedId ?? fromAssetId;
            var resolvedFromLabel = fromAsset.LabelCode ?? (string.IsNullOrWhiteSpace(fromAssetLabel) ? null : GetAssetLabelCode(fromAssetLabel));
            var resolvedFromDisplay = fromAsset.DisplayName;
            var resolvedToId = toAsset.ResolvedId ?? toAssetId;
            var resolvedToLabel = toAsset.LabelCode ?? (string.IsNullOrWhiteSpace(toAssetLabel) ? null : GetAssetLabelCode(toAssetLabel));
            var resolvedToDisplay = toAsset.DisplayName;
            var fromAssetIds = string.IsNullOrWhiteSpace(resolvedFromId) ? null : new List<string> { resolvedFromId };
            var fromAssetLabels = string.IsNullOrWhiteSpace(resolvedFromLabel) ? null : new List<string> { resolvedFromLabel };
            var fromAssetDisplayNames = string.IsNullOrWhiteSpace(resolvedFromDisplay) ? null : new List<string> { resolvedFromDisplay };
            var resolvedExpectedHash = expectedHash;
            AddonStateSnapshot? expectedSnapshot = null;

            if (string.IsNullOrWhiteSpace(resolvedExpectedHash) && !string.IsNullOrWhiteSpace(resolvedToId))
            {
                expectedSnapshot = CaptureExpectedStateSnapshotForAsset(resolvedToId);
                resolvedExpectedHash = expectedSnapshot != null ? ComputeStateHash(expectedSnapshot) : null;
            }

            if (!string.IsNullOrWhiteSpace(resolvedExpectedHash) && !resolved.HasValue)
            {
                resolved = string.Equals(finalHash, resolvedExpectedHash, StringComparison.Ordinal);
            }

            var result = errorCode != null
                ? "fail"
                : (resolved.HasValue ? (resolved.Value ? "success" : "fail") : "end");

            LogExperimentEvent(
                "BlSwitchEnd",
                eventScope: "external",
                targetId: blMethod ?? "bl",
                result: result,
                afterHash: finalHash,
                expectedHash: resolvedExpectedHash,
                finalHash: finalHash,
                blMethod: blMethod,
                note: note,
                errorCode: errorCode,
                fromAssetIds: fromAssetIds,
                fromAssetLabels: fromAssetLabels,
                fromAssetDisplayNames: fromAssetDisplayNames,
                toAssetId: resolvedToId,
                toAssetLabel: resolvedToLabel,
                toAssetDisplayName: resolvedToDisplay,
                stateHashScope: snapshot.Source,
                expectedHashScope: expectedSnapshot?.Source,
                fromAssetResolveMethod: fromAsset.ResolveMethod,
                toAssetResolveMethod: toAsset.ResolveMethod);

            return errorCode == null;
        }

        private sealed class StateMatchResult
        {
            public StateMatchResult(
                AddonStateSnapshot snapshot,
                AddonStateSnapshot expectedSnapshot,
                bool matched,
                long durationMs,
                ExperimentEventMetrics? metrics)
            {
                Snapshot = snapshot;
                ExpectedSnapshot = expectedSnapshot;
                Matched = matched;
                DurationMs = durationMs;
                Metrics = metrics;
            }

            public AddonStateSnapshot Snapshot { get; }
            public AddonStateSnapshot ExpectedSnapshot { get; }
            public bool Matched { get; }
            public long DurationMs { get; }
            public ExperimentEventMetrics? Metrics { get; }
            public bool RuntimeWriteSucceeded { get; set; } = true;
            public string? FailureCode { get; set; }
            public string? AttributionConflictOperationId { get; set; }
            public string? SupersededConflictOperationId { get; set; }
            public bool Succeeded =>
                Matched &&
                RuntimeWriteSucceeded &&
                string.IsNullOrWhiteSpace(FailureCode);
        }

        private sealed class LinkOperationMetrics
        {
            private long hardlinkCount;
            private long junctionCount;
            private long copyBytes;
            private long filesTouched;

            public void RecordHardlink()
            {
                Interlocked.Increment(ref hardlinkCount);
                Interlocked.Increment(ref filesTouched);
            }

            public void RecordJunction()
            {
                Interlocked.Increment(ref junctionCount);
                Interlocked.Increment(ref filesTouched);
            }

            public void RecordCopy(long bytes)
            {
                Interlocked.Add(ref copyBytes, bytes);
                Interlocked.Increment(ref filesTouched);
            }

            public ExperimentEventMetrics? ToEventMetrics()
            {
                var hardlinks = (int)Interlocked.Read(ref hardlinkCount);
                var junctions = (int)Interlocked.Read(ref junctionCount);
                var copies = Interlocked.Read(ref copyBytes);
                var files = (int)Interlocked.Read(ref filesTouched);

                if (hardlinks == 0 && junctions == 0 && copies == 0 && files == 0)
                {
                    return null;
                }

                return new ExperimentEventMetrics
                {
                    LinkCreatedHardlinkCount = hardlinks,
                    LinkCreatedJunctionCount = junctions,
                    CopyBytes = copies,
                    FilesTouchedCount = files
                };
            }
        }

        private bool ApplyAddonStateStoreBulk(
            Dictionary<string, bool> expectedStates,
            bool allowConflictSupersede)
        {
            if (expectedStates == null || expectedStates.Count == 0)
            {
                return true;
            }

            if (gmodAddonStateStore == null)
            {
                errorHandler.HandleWarning(
                    "Garry's Mod settings path is unknown; addonnomount.txt will not be updated.",
                    "UpdateAddonStates");
                return false;
            }

            if (DeferRuntimeApplyIfGmodRunning(allowConflictSupersede))
            {
                return false;
            }

            var filteredStates = expectedStates
                .Where(kvp => IsWorkshopNumericId(kvp.Key))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);

            if (filteredStates.Count == 0)
            {
                return true;
            }

            return ExecuteJournaledRuntimeWrite(
                filteredStates,
                () => gmodAddonStateStore.SetEnabledBulk(filteredStates),
                "UpdateAddonStates");
        }

        private bool TryPersistSingleAddonRuntimeState(string addonId, bool enabled, string operationName)
        {
            if (DeferRuntimeApplyIfGmodRunning(
                    allowConflictSupersede: true))
            {
                return false;
            }

            if (gmodAddonStateStore == null)
            {
                errorHandler.HandleWarning("Garry's Mod settings path is unknown; addonnomount.txt will not be updated.", operationName);
                return false;
            }

            var target = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                [addonId] = enabled
            };
            return ExecuteJournaledRuntimeWrite(
                target,
                () => gmodAddonStateStore.SetEnabled(addonId, enabled),
                operationName);
        }

        private bool ExecuteJournaledRuntimeWrite(
            IReadOnlyDictionary<string, bool> targetStates,
            Func<bool> writeRuntimeState,
            string operationName)
        {
            if (gmodAddonStateStore == null)
            {
                return false;
            }

            AddonMountSnapshot beforeSnapshot;
            try
            {
                beforeSnapshot = gmodAddonStateStore.ReadSnapshot();
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning(
                    $"Failed to read addonnomount.txt before GAM runtime write: {ex.Message}",
                    operationName);
                return false;
            }

            if (!beforeSnapshot.IsValidFormat)
            {
                errorHandler.HandleWarning(
                    "Refusing to write malformed addonnomount.txt; attribution baseline was not advanced.",
                    operationName);
                return false;
            }

            var disabledBefore = new HashSet<string>(
                beforeSnapshot.DisabledIds,
                StringComparer.Ordinal);
            var previousStates = targetStates.Keys.ToDictionary(
                id => id,
                id => !disabledBefore.Contains(id),
                StringComparer.Ordinal);
            var configurationBeforeIntent = CloneConfiguration(configuration);
            var pendingIntent = gmodDisabledAddonReconciliationService.CreatePendingWrite(
                targetStates,
                previousStates,
                DateTime.UtcNow,
                gmodAddonStateStore.NoMountFilePath);
            configuration.PendingGamRuntimeWrite = pendingIntent;

            try
            {
                SaveConfigurationImmediatelySynchronously();
            }
            catch (Exception ex)
            {
                configuration = configurationBeforeIntent;
                errorHandler.HandleWarning(
                    $"Could not journal GAM runtime write; addonnomount.txt was not changed: {ex.Message}",
                    operationName);
                return false;
            }

            bool persisted;
            try
            {
                persisted = writeRuntimeState();
            }
            catch (Exception ex)
            {
                persisted = false;
                errorHandler.HandleWarning(
                    $"Failed to update addonnomount.txt: {ex.Message}",
                    operationName);
            }

            if (!persisted)
            {
                // Keep the already-durable intent. Recovery can prove that the
                // pre-write state survived, queue the still-durable desired state,
                // and avoid misclassifying it as a GMod-originated transition.
                errorHandler.HandleWarning(
                    "Failed to persist addon states to addonnomount.txt; attribution baseline was not advanced.",
                    operationName);
                return false;
            }

            gmodDisabledAddonReconciliationService.RecordSuccessfulGamWrite(
                configuration,
                targetStates,
                DateTime.UtcNow,
                gmodAddonStateStore.NoMountFilePath);
            try
            {
                SaveConfigurationImmediatelySynchronously();
                return true;
            }
            catch (Exception ex)
            {
                // Runtime already changed. Restore the durable pending intent in
                // memory; the next valid observation can complete attribution.
                configuration = configurationBeforeIntent;
                configuration.PendingGamRuntimeWrite = pendingIntent;
                errorHandler.HandleWarning(
                    $"GAM changed addonnomount.txt but could not finalize its attribution journal: {ex.Message}",
                    operationName);
                return false;
            }
        }

        private async Task<StateMatchResult> UpdateAddonStatesInternalAsync(
            bool logEvents,
            string? parentOperationId = null,
            IProgress<(int current, int total)>? progress = null,
            bool observeExternalChanges = true,
            bool allowConflictSupersede = true)
        {
            await _runtimeReconcileGate.WaitAsync();
            try
            {
                return await UpdateAddonStatesCoreAsync(
                    logEvents,
                    parentOperationId,
                    progress,
                    observeExternalChanges,
                    allowConflictSupersede);
            }
            finally
            {
                _runtimeReconcileGate.Release();
            }
        }

        private async Task<StateMatchResult> UpdateAddonStatesCoreAsync(
            bool logEvents,
            string? parentOperationId,
            IProgress<(int current, int total)>? progress,
            bool observeExternalChanges,
            bool allowConflictSupersede = true)
        {
            if (!TryGetSubscribedAddonIdSet(
                    "UpdateAddonStates",
                    out var subscribedAddonIds))
            {
                errorHandler.HandleWarning(
                    "Steam subscription state is unavailable; runtime addon state was not changed.",
                    "UpdateAddonStates");
                var unavailableActual = CaptureState();
                var unavailableExpected = BuildSnapshot(
                    new Dictionary<string, bool>(StringComparer.Ordinal),
                    "expected:steam-subscription-unavailable");
                return new StateMatchResult(
                    unavailableActual,
                    unavailableExpected,
                    matched: false,
                    durationMs: 0,
                    metrics: null)
                {
                    RuntimeWriteSucceeded = false,
                    FailureCode = "subscription_state_unavailable"
                };
            }

            Guid? recoveredMarkerGeneration = null;
            string? supersededConflictOperationId = null;
            if (observeExternalChanges)
            {
                var observation = await TryReconcileGmodDisabledAddonsFromRuntimeAsync(
                    "UpdateAddonStates",
                    subscribedAddonIds);
                recoveredMarkerGeneration =
                    observation?.QueuedRuntimeApplyGeneration;
                if (observation?.PendingRecovery ==
                    PendingGamRuntimeWriteRecovery.Conflicted)
                {
                    supersededConflictOperationId =
                        observation.PendingOperationId;
                    if (allowConflictSupersede)
                    {
                        errorHandler.HandleInfo(
                            "An explicit GAM operation is superseding a previously latched runtime-attribution conflict.",
                            "UpdateAddonStates");
                    }
                    else
                    {
                    var conflictedActual = CaptureState();
                    var conflictedExpected = BuildSnapshot(
                        new Dictionary<string, bool>(StringComparer.Ordinal),
                        "expected:runtime-attribution-conflict");
                    return new StateMatchResult(
                        conflictedActual,
                        conflictedExpected,
                        matched: false,
                        durationMs: 0,
                        metrics: null)
                    {
                        RuntimeWriteSucceeded = false,
                        FailureCode = RuntimeAttributionConflictFailureCode,
                        AttributionConflictOperationId =
                            observation.PendingOperationId
                    };
                    }
                }
            }

            var expectedStates = BuildExpectedStatesForAssets(
                configuration.Assets,
                subscribedAddonIds);
            var allAddonIds = expectedStates.Keys.ToList();

            var totalAddons = allAddonIds.Count;
            progress?.Report((0, totalAddons));

            var linkMetrics = logEvents ? new LinkOperationMetrics() : null;
            linkMetricsContext.Value = linkMetrics;

            string? operationId = logEvents ? eventLogger.NewOperationId() : null;
            AddonStateSnapshot? beforeSnapshot = null;
            string? beforeHash = null;
            string? beforeHashScope = null;

            if (logEvents)
            {
                beforeSnapshot = CaptureState();
                beforeHash = ComputeStateHash(beforeSnapshot);
                beforeHashScope = beforeSnapshot.Source;
                LogExperimentEvent(
                    "UpdateAddonStatesStart",
                    eventScope: "system",
                    targetId: "all",
                    result: "start",
                    beforeHash: beforeHash,
                    operationId: operationId,
                    parentOperationId: parentOperationId,
                    stateHashScope: beforeHashScope);
            }

            var stopwatch = Stopwatch.StartNew();

            Exception? updateError = null;
            var runtimeWriteSucceeded = false;
            try
            {
                var progressCurrent = 0;
                bulkStateUpdateDepth.Value++;
                try
                {
                    using var semaphore = new SemaphoreSlim(_maxParallelAddonStateUpdates, _maxParallelAddonStateUpdates);
                    var tasks = allAddonIds.Select(async addonId =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            var finalState = expectedStates.TryGetValue(addonId, out var state)
                                ? state
                                : CalculateFinalAddonState(addonId);

                            try
                            {
                                if (finalState)
                                {
                                    EnableAddon(addonId);
                                }
                                else
                                {
                                    DisableAddon(addonId);
                                }
                            }
                            catch (Exception ex)
                            {
                                if (FindStrictLinkModeException(ex) != null)
                                {
                                    throw;
                                }

                                errorHandler.HandleError(ex, $"Failed to update addon state for {addonId}", ErrorSeverity.Warning);
                            }
                        }
                        finally
                        {
                            semaphore.Release();
                            if (progress != null)
                            {
                                var completed = Interlocked.Increment(ref progressCurrent);
                                progress.Report((completed, totalAddons));
                            }
                        }
                    }).ToList();

                    await Task.WhenAll(tasks);
                }
                catch (Exception ex)
                {
                    updateError = ex;
                    if (FindStrictLinkModeException(ex) != null)
                    {
                        var afterSnapshot = CaptureState();
                        var expectedSnapshot = BuildSnapshot(expectedStates, GetExpectedScopeLabel(assetSpecific: false));
                        var afterHash = ComputeStateHash(afterSnapshot);
                        var expectedHash = ComputeStateHash(expectedSnapshot);
                        var metricsSnapshot = linkMetrics?.ToEventMetrics();

                        if (logEvents)
                        {
                            LogExperimentEvent(
                                "UpdateAddonStatesEnd",
                                eventScope: "system",
                                targetId: "all",
                                result: "fail",
                                durationMs: stopwatch.ElapsedMilliseconds,
                                beforeHash: beforeHash,
                                afterHash: afterHash,
                                expectedHash: expectedHash,
                                errorCode: "strict_link_copy_blocked",
                                operationId: operationId,
                                metrics: metricsSnapshot,
                                parentOperationId: parentOperationId,
                                stateHashScope: afterSnapshot.Source,
                                expectedHashScope: expectedSnapshot.Source);
                        }

                        throw;
                    }
                }
                finally
                {
                    runtimeWriteSucceeded = ApplyAddonStateStoreBulk(
                        expectedStates,
                        allowConflictSupersede);
                    bulkStateUpdateDepth.Value--;
                    InvalidateWorkshopScanCache();
                }

                var metrics = linkMetrics?.ToEventMetrics();
                var matchResult = await WaitForExpectedStateAsync(expectedStates, metrics);
                matchResult.RuntimeWriteSucceeded =
                    runtimeWriteSucceeded &&
                    updateError == null;
                matchResult.FailureCode = updateError != null
                    ? "update_failed"
                    : runtimeWriteSucceeded
                        ? null
                        : "runtime_write_failed";
                matchResult.SupersededConflictOperationId =
                    supersededConflictOperationId;
                stopwatch.Stop();

                if (eventLogger.IsExperimentContextActive)
                {
                    await SaveConfigurationImmediatelyAsync();
                }

                if (logEvents)
                {
                    var afterHash = ComputeStateHash(matchResult.Snapshot);
                    var expectedHash = ComputeStateHash(matchResult.ExpectedSnapshot);
                    var result = matchResult.Succeeded ? "success" : "fail";
                    var errorCode = matchResult.FailureCode ??
                                    (matchResult.Matched ? null : "state_mismatch");

                    LogExperimentEvent(
                        "UpdateAddonStatesEnd",
                        eventScope: "system",
                        targetId: "all",
                        result: result,
                        durationMs: stopwatch.ElapsedMilliseconds,
                        beforeHash: beforeHash,
                        afterHash: afterHash,
                        expectedHash: expectedHash,
                        errorCode: errorCode,
                        operationId: operationId,
                        metrics: matchResult.Metrics,
                        parentOperationId: parentOperationId,
                        stateHashScope: matchResult.Snapshot.Source,
                        expectedHashScope: matchResult.ExpectedSnapshot.Source);
                }

                if (matchResult.Succeeded &&
                    recoveredMarkerGeneration.HasValue &&
                    ClearRuntimeApplyIfGenerationProvider != null)
                {
                    try
                    {
                        _ = ClearRuntimeApplyIfGenerationProvider.Invoke(
                            recoveredMarkerGeneration.Value);
                    }
                    catch (Exception ex)
                    {
                        // The completed runtime state is already durable. Leaving
                        // this exact marker behind is a safe redundant retry.
                        errorHandler.HandleWarning(
                            $"The recovered runtime apply marker could not be cleared: {ex.Message}",
                            "UpdateAddonStates");
                    }
                }

                return matchResult;
            }
            finally
            {
                linkMetricsContext.Value = null;
            }
        }

        private async Task<StateMatchResult> WaitForExpectedStateAsync(
            Dictionary<string, bool> expectedStates,
            ExperimentEventMetrics? metrics)
        {
            var expectedSnapshot = BuildSnapshot(expectedStates, GetExpectedScopeLabel(assetSpecific: false));
            var expectedNormalized = expectedSnapshot.NormalizedState;
            var stopwatch = Stopwatch.StartNew();

            var currentSnapshot = CaptureStateForExpectedScope(expectedStates.Keys);
            var pollInterval = Math.Max(50, StateMatchPollIntervalMs);

            if (StateMatchTimeout <= TimeSpan.Zero)
            {
                var matched = string.Equals(currentSnapshot.NormalizedState, expectedNormalized, StringComparison.Ordinal);
                return new StateMatchResult(currentSnapshot, expectedSnapshot, matched, stopwatch.ElapsedMilliseconds, metrics);
            }

            while (stopwatch.Elapsed < StateMatchTimeout)
            {
                if (string.Equals(currentSnapshot.NormalizedState, expectedNormalized, StringComparison.Ordinal))
                {
                    return new StateMatchResult(currentSnapshot, expectedSnapshot, true, stopwatch.ElapsedMilliseconds, metrics);
                }

                await Task.Delay(pollInterval);
                currentSnapshot = CaptureStateForExpectedScope(expectedStates.Keys);
            }

            var finalMatch = string.Equals(currentSnapshot.NormalizedState, expectedNormalized, StringComparison.Ordinal);
            return new StateMatchResult(currentSnapshot, expectedSnapshot, finalMatch, stopwatch.ElapsedMilliseconds, metrics);
        }

        private AddonStateSnapshot CaptureStateForExpectedScope(
            IEnumerable<string> expectedAddonIds)
        {
            var actualSnapshot = CaptureState();
            var expectedIdSet = new HashSet<string>(
                expectedAddonIds ?? Enumerable.Empty<string>(),
                StringComparer.Ordinal);
            var projectedStates = actualSnapshot.States
                .Where(entry => expectedIdSet.Contains(entry.Key))
                .ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value,
                    StringComparer.Ordinal);

            return BuildSnapshot(
                projectedStates,
                actualSnapshot.Source + ":managed-scope");
        }
        
        /// <summary>
        // Initialize WorkshopIconResolver
        private void UpdateAddonStates()
        {
            var wasGmodRunning = IsGmodCurrentlyRunning();
            var applied = Task.Run(() => UpdateAddonStatesAsync())
                .GetAwaiter()
                .GetResult();
            if (!applied && !wasGmodRunning)
            {
                throw new InvalidOperationException(
                    "The desired addon state was saved, but runtime reconciliation did not complete.");
            }
        }

        private void SaveConfigurationImmediatelySynchronously()
        {
            Task.Run(() => SaveConfigurationImmediatelyAsync())
                .GetAwaiter()
                .GetResult();
        }

        private static Configuration CloneConfiguration(Configuration source)
        {
            var json = JsonConvert.SerializeObject(source);
            return JsonConvert.DeserializeObject<Configuration>(json)
                ?? throw new InvalidOperationException(
                    "Failed to clone configuration for transactional rollback.");
        }
        
        /// <summary>
        // Initialize WorkshopIconResolver
        private bool CalculateFinalAddonState(string addonId)
        {
            if (!TryGetSubscribedAddonIdSet(
                    "CalculateFinalAddonState",
                    out var runtimeTargets))
            {
                throw new InvalidOperationException(
                    "Steam subscription state is unavailable; addon state cannot be safely applied.");
            }

            return assetStateResolver
                .Resolve(addonId, configuration.Assets, runtimeTargets)
                .DesiredEnabled;
        }
        
        /// <summary>
        // Initialize WorkshopIconResolver
        public void SetAddonState(string assetId, string addonId, AddonState state)
        {
            if (GmodDisabledAddonReconciliationService.IsProtectedSystemAsset(assetId))
            {
                return;
            }

            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId);
            if (asset != null && (asset.Addons.Contains(addonId) || asset.ContainsAllAddons()))
            {
                var operationId = eventLogger.NewOperationId();
                var beforeSnapshot = CaptureState();
                var beforeHash = ComputeStateHash(beforeSnapshot);
                var stopwatch = Stopwatch.StartNew();

                // Initialize WorkshopIconResolver
                var previousState = asset.GetAddonState(addonId);
                
                // Undo險倬鹸
                var addonInfo = configuration.AddonMetadata.ContainsKey(addonId) 
                    ? configuration.AddonMetadata[addonId] 
                    : null;
                var addonName = addonInfo?.Title ?? addonId;
                
                var undoAction = new UndoAction(
                    UndoActionType.AddonStateChanged, 
                    $"Changed '{addonName}' state to {GetStateDisplayName(state)}")
                {
                    AssetId = assetId,
                    AssetName = asset.Name,
                    AddonId = addonId,
                    AddonName = addonName,
                    PreviousAddonState = previousState,
                    NewAddonState = state,
                    AffectedAddonIds = new List<string> { addonId },
                    PreviousAddonStates = new Dictionary<string, AddonState>
                    {
                        [addonId] = previousState
                    }
                };
                undoManager.RecordAction(undoAction);
                
                asset.SetAddonState(addonId, state);
                try
                {
                    // This legacy synchronous API still uses the authoritative
                    // full reconcile so external observation, journaling and
                    // attribution share one serialized runtime transaction.
                    UpdateAddonStates();

                    stopwatch.Stop();
                    var afterSnapshot = CaptureState();
                    var afterHash = ComputeStateHash(afterSnapshot);

                    LogExperimentEvent(
                        "AddonToggle",
                        eventScope: "user",
                        targetId: addonId,
                        result: "success",
                        durationMs: stopwatch.ElapsedMilliseconds,
                        beforeHash: beforeHash,
                        afterHash: afterHash,
                        operationId: operationId,
                        assetId: assetId);
                }
                catch (Exception ex)
                {
                    asset.SetAddonState(addonId, previousState);
                    undoManager.RemoveAction(undoAction);
                    stopwatch.Stop();
                    var afterSnapshot = CaptureState();
                    var afterHash = ComputeStateHash(afterSnapshot);
                    var errorCode = FindStrictLinkModeException(ex) != null
                        ? "strict_link_copy_blocked"
                        : "toggle_failed";

                    LogExperimentEvent(
                        "AddonToggle",
                        eventScope: "user",
                        targetId: addonId,
                        result: "fail",
                        durationMs: stopwatch.ElapsedMilliseconds,
                        beforeHash: beforeHash,
                        afterHash: afterHash,
                        errorCode: errorCode,
                        operationId: operationId,
                        assetId: assetId);

                    throw;
                }
            }
        }

        public void SetAddonStatesBatch(string assetId, List<string> addonIds, AddonState state, IProgress<(int current, int total)>? progress = null)
        {
            if (GmodDisabledAddonReconciliationService.IsProtectedSystemAsset(assetId))
            {
                return;
            }

            if (addonIds == null || addonIds.Count == 0)
            {
                return;
            }

            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId);
            if (asset == null)
            {
                return;
            }

            var targetIds = addonIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .Where(id => asset.ContainsAllAddons() || asset.Addons.Contains(id))
                .ToList();

            if (targetIds.Count == 0)
            {
                return;
            }

            var previousAddonStates = new Dictionary<string, AddonState>();
            foreach (var addonId in targetIds)
            {
                previousAddonStates[addonId] = asset.GetAddonState(addonId);
            }

            undoManager.RecordAction(new UndoAction(
                UndoActionType.AddonStateChanged,
                $"Changed {targetIds.Count} addons to {GetStateDisplayName(state)}")
            {
                AssetId = assetId,
                AssetName = asset.Name,
                AffectedAddonIds = targetIds,
                PreviousAddonStates = previousAddonStates,
                NewAddonState = state
            });

            var total = targetIds.Count;
            progress?.Report((0, total));

            var current = 0;
            foreach (var addonId in targetIds)
            {
                asset.SetAddonState(addonId, state);
                current++;
                progress?.Report((current, total));
            }
        }
        
        private string GetStateDisplayName(AddonState state)
        {
            switch (state)
            {
                case AddonState.Enabled: return "Enabled";
                case AddonState.Disabled: return "Disabled";
                case AddonState.Excluded: return "Excluded";
                default: return state.ToString();
            }
        }
        
        /// <summary>
        // Initialize WorkshopIconResolver
        /// </summary>
        private void UpdateSingleAddonState(string addonId)
        {
            try
            {
                // Initialize WorkshopIconResolver
                if (addonId == "*")
                {
                    // Initialize WorkshopIconResolver
                    errorHandler.HandleInfo("Skipping wildcard addon ID '*' (this is normal behavior)", "UpdateSingleAddonState");
                    return;
                }
                
                // Initialize WorkshopIconResolver
                if (!configuration.AddonMetadata.ContainsKey(addonId))
                {
                    errorHandler.HandleWarning($"Addon not found: {addonId}", "UpdateSingleAddonState");
                    return;
                }
                
                var finalState = CalculateFinalAddonState(addonId);
                
                if (finalState)
                {
                    EnableAddon(addonId);
                }
                else
                {
                    DisableAddon(addonId);
                }
            }
            catch (Exception ex)
            {
                if (FindStrictLinkModeException(ex) != null)
                {
                    throw;
                }

                errorHandler.HandleError(ex, $"Failed to update addon state for {addonId}", ErrorSeverity.Warning);
                throw;
            }
        }
        
        /// <summary>
        // Initialize WorkshopIconResolver
        public UndoManager GetUndoManager() => undoManager;
        
        /// <summary>
        // Initialize WorkshopIconResolver
        public async Task<bool> UndoLastActionAsync()
        {
            var action = undoManager.PopLastAction();
            if (action == null) return false;

            var operationId = eventLogger.NewOperationId();
            var beforeSnapshot = CaptureState();
            var beforeHash = ComputeStateHash(beforeSnapshot);
            LogExperimentEvent(
                "UndoStart",
                eventScope: "user",
                targetId: action.Id,
                result: "start",
                beforeHash: beforeHash,
                operationId: operationId);

            var stopwatch = Stopwatch.StartNew();
            bool success = false;
            string? errorCode = null;
            StrictLinkModeException? strictFailure = null;
            var actionHandled = false;
            var configurationPersisted = false;
            var requiresRuntimeReconcile = false;
            var previousLastUpdated = configuration.LastUpdated;
            Action? rollbackMutation = null;

            try
            {
                switch (action.Type)
                {
                    case UndoActionType.AssetCreated:
                        // Initialize WorkshopIconResolver
                        if (action.AssetId != null)
                        {
                            var assetIndex = configuration.Assets.FindIndex(a => a.Id == action.AssetId);
                            if (assetIndex >= 0)
                            {
                                var removedAsset = configuration.Assets[assetIndex];
                                rollbackMutation = () =>
                                {
                                    if (configuration.Assets.All(a => a.Id != removedAsset.Id))
                                    {
                                        configuration.Assets.Insert(
                                            Math.Min(assetIndex, configuration.Assets.Count),
                                            removedAsset);
                                    }
                                };
                                configuration.Assets.RemoveAt(assetIndex);
                                actionHandled = true;
                                requiresRuntimeReconcile = true;
                            }
                        }
                        break;
                        
                    case UndoActionType.AssetDeleted:
                        // Initialize WorkshopIconResolver
                        if (action.DeletedAsset != null &&
                            configuration.Assets.All(a => a.Id != action.DeletedAsset.Id))
                        {
                            var restoredAsset = action.DeletedAsset;
                            rollbackMutation = () => configuration.Assets.Remove(restoredAsset);
                            configuration.Assets.Add(restoredAsset);
                            actionHandled = true;
                            requiresRuntimeReconcile = true;
                        }
                        break;
                        
                    case UndoActionType.AssetEnabled:
                    case UndoActionType.AssetDisabled:
                    case UndoActionType.AssetExcluded:
                        // Initialize WorkshopIconResolver
                        if (action.AssetId != null)
                        {
                            var asset = configuration.Assets.FirstOrDefault(a => a.Id == action.AssetId);
                            if (asset != null)
                            {
                                var currentState = asset.GetWholeState();
                                rollbackMutation = () => asset.SetWholeState(currentState);
                                if (action.PreviousDefaultAddonState.HasValue)
                                {
                                    asset.SetWholeState(action.PreviousDefaultAddonState.Value);
                                }
                                else if (action.PreviousEnabledState.HasValue)
                                {
                                    asset.SetWholeState(
                                        action.PreviousEnabledState.Value
                                            ? AddonState.Enabled
                                            : AddonState.Disabled);
                                }
                                
                                actionHandled = true;
                                requiresRuntimeReconcile = true;
                            }
                        }
                        break;
                        
                    case UndoActionType.AddonStateChanged:
                                // どのアセットにも属しておらず、ジャンクションが存在しない = 孤立した無効化アドオン
                        if (action.AssetId != null)
                        {
                            var asset = configuration.Assets.FirstOrDefault(a => a.Id == action.AssetId);
                            if (asset != null)
                            {
                                var currentState = asset.GetWholeState();
                                rollbackMutation = () => asset.SetWholeState(currentState);
                                if (action.AffectedAddonIds != null && action.PreviousAddonStates != null)
                                {
                                    foreach (var addonId in action.AffectedAddonIds)
                                    {
                                        if (action.PreviousAddonStates.TryGetValue(addonId, out var previousState))
                                        {
                                            asset.SetAddonState(addonId, previousState);
                                        }
                                    }
                                }
                                else if (action.AddonId != null && action.PreviousAddonState.HasValue)
                                {
                                    asset.SetAddonState(action.AddonId, action.PreviousAddonState.Value);
                                }

                                actionHandled = true;
                                requiresRuntimeReconcile = true;
                            }
                        }
                        break;
                        
                    case UndoActionType.AddonAddedToAsset:
                        // Initialize WorkshopIconResolver
                        if (action.AssetId != null)
                        {
                            var asset = configuration.Assets.FirstOrDefault(a => a.Id == action.AssetId);
                            if (asset != null)
                            {
                                var currentAddons = asset.Addons.ToList();
                                var currentAddonStates =
                                    new Dictionary<string, AddonState>(asset.AddonStates);
                                rollbackMutation = () =>
                                {
                                    asset.Addons = currentAddons;
                                    asset.AddonStates = currentAddonStates;
                                };
                                var addonIds = action.AffectedAddonIds;
                                if ((addonIds == null || addonIds.Count == 0) && !string.IsNullOrEmpty(action.AddonId))
                                {
                                    addonIds = action.AddonId.Split(',').Select(id => id.Trim()).Where(id => id.Length > 0).ToList();
                                }

                                if (addonIds != null)
                                {
                                    foreach (var addonId in addonIds)
                                    {
                                        asset.RemoveAddon(addonId);
                                    }
                                }

                                actionHandled = true;
                                requiresRuntimeReconcile = true;
                            }
                        }
                        break;
                        
                    case UndoActionType.AddonRemovedFromAsset:
                        // Initialize WorkshopIconResolver
                        if (action.AssetId != null)
                        {
                            var asset = configuration.Assets.FirstOrDefault(a => a.Id == action.AssetId);
                            if (asset != null)
                            {
                                var currentAddons = asset.Addons.ToList();
                                var currentAddonStates =
                                    new Dictionary<string, AddonState>(asset.AddonStates);
                                rollbackMutation = () =>
                                {
                                    asset.Addons = currentAddons;
                                    asset.AddonStates = currentAddonStates;
                                };
                                if (action.AffectedAddonIds != null && action.PreviousAddonStates != null)
                                {
                                    foreach (var addonId in action.AffectedAddonIds)
                                    {
                                        if (action.PreviousAddonStates.TryGetValue(addonId, out var previousState))
                                        {
                                            asset.AddAddon(addonId, previousState);
                                        }
                                    }
                                }
                                else if (action.AddonId != null && action.AddonState.HasValue)
                                {
                                    asset.AddAddon(action.AddonId, action.AddonState.Value);
                                }

                                actionHandled = true;
                                requiresRuntimeReconcile = true;
                            }
                        }
                        break;

                    case UndoActionType.AssetEdited:
                        if (action.AssetId != null)
                        {
                            var asset = configuration.Assets.FirstOrDefault(a => a.Id == action.AssetId);
                            if (asset != null)
                            {
                                var currentName = asset.Name;
                                var currentImageReference = asset.ImagePath;
                                var currentImagePath = ResolveAssetImagePath(asset);
                                asset.ImagePath = action.PreviousImagePath;
                                var previousImagePath = ResolveAssetImagePath(asset);
                                asset.ImagePath = currentImageReference;

                                var imageSnapshots =
                                    new Dictionary<string, (bool Exists, byte[]? Bytes, FileAttributes Attributes)>(
                                        StringComparer.OrdinalIgnoreCase);
                                if (action.AssetImageChanged)
                                {
                                    foreach (var path in new[] { currentImagePath, previousImagePath }
                                                 .Where(path => !string.IsNullOrWhiteSpace(path))
                                                 .Cast<string>()
                                                 .Distinct(StringComparer.OrdinalIgnoreCase))
                                    {
                                        var exists = File.Exists(path);
                                        imageSnapshots[path] = exists
                                            ? (true, File.ReadAllBytes(path), File.GetAttributes(path))
                                            : (false, null, FileAttributes.Normal);
                                    }
                                }

                                rollbackMutation = () =>
                                {
                                    asset.Name = currentName;
                                    asset.ImagePath = currentImageReference;
                                    foreach (var snapshot in imageSnapshots)
                                    {
                                        if (!snapshot.Value.Exists)
                                        {
                                            if (File.Exists(snapshot.Key))
                                            {
                                                File.SetAttributes(snapshot.Key, FileAttributes.Normal);
                                                File.Delete(snapshot.Key);
                                            }
                                            continue;
                                        }

                                        var directory = Path.GetDirectoryName(snapshot.Key);
                                        if (!string.IsNullOrWhiteSpace(directory))
                                        {
                                            Directory.CreateDirectory(directory);
                                        }
                                        File.WriteAllBytes(snapshot.Key, snapshot.Value.Bytes!);
                                        File.SetAttributes(snapshot.Key, snapshot.Value.Attributes);
                                    }
                                };

                                if (action.AssetNameChanged &&
                                    action.PreviousAssetName != null)
                                {
                                    asset.Name = action.PreviousAssetName;
                                }

                                if (action.AssetImageChanged)
                                {
                                    if (!string.IsNullOrWhiteSpace(currentImagePath) &&
                                        File.Exists(currentImagePath))
                                    {
                                        File.SetAttributes(currentImagePath, FileAttributes.Normal);
                                        File.Delete(currentImagePath);
                                    }

                                    asset.ImagePath = action.PreviousImagePath;
                                    if (action.PreviousImageBytes != null &&
                                        !string.IsNullOrWhiteSpace(previousImagePath))
                                    {
                                        var directory = Path.GetDirectoryName(previousImagePath);
                                        if (!string.IsNullOrWhiteSpace(directory))
                                        {
                                            Directory.CreateDirectory(directory);
                                        }
                                        File.WriteAllBytes(
                                            previousImagePath,
                                            action.PreviousImageBytes);
                                    }
                                }

                                actionHandled = true;
                            }
                        }
                        break;

                    case UndoActionType.AssetRenamed:
                        if (action.AssetId != null && action.PreviousAssetName != null)
                        {
                            var asset = configuration.Assets.FirstOrDefault(a => a.Id == action.AssetId);
                            if (asset != null)
                            {
                                var currentName = asset.Name;
                                rollbackMutation = () => asset.Name = currentName;
                                asset.Name = action.PreviousAssetName;
                                actionHandled = true;
                            }
                        }
                        break;

                    case UndoActionType.AssetFavoriteChanged:
                        if (action.AssetId != null && action.PreviousFavoriteState.HasValue)
                        {
                            var asset = configuration.Assets.FirstOrDefault(a => a.Id == action.AssetId);
                            if (asset != null)
                            {
                                var currentFavorite = asset.IsFavorite;
                                rollbackMutation = () => asset.IsFavorite = currentFavorite;
                                asset.IsFavorite = action.PreviousFavoriteState.Value;
                                actionHandled = true;
                            }
                        }
                        break;

                    case UndoActionType.AssetImageChanged:
                        if (action.AssetId != null)
                        {
                            var asset = configuration.Assets.FirstOrDefault(a => a.Id == action.AssetId);
                            if (asset != null)
                            {
                                var currentImageReference = asset.ImagePath;
                                var currentImagePath = ResolveAssetImagePath(asset);
                                asset.ImagePath = action.PreviousImagePath;
                                var previousImagePath = ResolveAssetImagePath(asset);
                                asset.ImagePath = currentImageReference;

                                var imageSnapshots =
                                    new Dictionary<string, (bool Exists, byte[]? Bytes, FileAttributes Attributes)>(
                                        StringComparer.OrdinalIgnoreCase);
                                foreach (var path in new[] { currentImagePath, previousImagePath }
                                             .Where(path => !string.IsNullOrWhiteSpace(path))
                                             .Cast<string>()
                                             .Distinct(StringComparer.OrdinalIgnoreCase))
                                {
                                    var exists = File.Exists(path);
                                    imageSnapshots[path] = exists
                                        ? (true, File.ReadAllBytes(path), File.GetAttributes(path))
                                        : (false, null, FileAttributes.Normal);
                                }

                                rollbackMutation = () =>
                                {
                                    asset.ImagePath = currentImageReference;
                                    foreach (var snapshot in imageSnapshots)
                                    {
                                        if (!snapshot.Value.Exists)
                                        {
                                            if (File.Exists(snapshot.Key))
                                            {
                                                File.SetAttributes(snapshot.Key, FileAttributes.Normal);
                                                File.Delete(snapshot.Key);
                                            }
                                            continue;
                                        }

                                        var directory = Path.GetDirectoryName(snapshot.Key);
                                        if (!string.IsNullOrWhiteSpace(directory))
                                        {
                                            Directory.CreateDirectory(directory);
                                        }
                                        File.WriteAllBytes(snapshot.Key, snapshot.Value.Bytes!);
                                        File.SetAttributes(snapshot.Key, snapshot.Value.Attributes);
                                    }
                                };

                                if (!string.IsNullOrWhiteSpace(currentImagePath) && File.Exists(currentImagePath))
                                {
                                    File.SetAttributes(currentImagePath, FileAttributes.Normal);
                                    File.Delete(currentImagePath);
                                }

                                asset.ImagePath = action.PreviousImagePath;
                                if (action.PreviousImageBytes != null &&
                                    !string.IsNullOrWhiteSpace(previousImagePath))
                                {
                                    var imageDirectory = Path.GetDirectoryName(previousImagePath);
                                    if (!string.IsNullOrWhiteSpace(imageDirectory))
                                    {
                                        Directory.CreateDirectory(imageDirectory);
                                    }
                                    File.WriteAllBytes(previousImagePath, action.PreviousImageBytes);
                                }
                                actionHandled = true;
                            }
                        }
                        break;

                    case UndoActionType.AllOff:
                        if (action.PreviousAssetStates != null)
                        {
                            var currentStates = configuration.Assets
                                .Where(asset => action.PreviousAssetStates.ContainsKey(asset.Id))
                                .ToDictionary(
                                    asset => asset.Id,
                                    asset => asset.GetWholeState(),
                                    StringComparer.Ordinal);
                            rollbackMutation = () =>
                            {
                                foreach (var current in currentStates)
                                {
                                    configuration.Assets
                                        .FirstOrDefault(asset => asset.Id == current.Key)
                                        ?.SetWholeState(current.Value);
                                }
                            };
                            foreach (var previous in action.PreviousAssetStates)
                            {
                                var asset = configuration.Assets.FirstOrDefault(a => a.Id == previous.Key);
                                asset?.SetWholeState(previous.Value);
                            }
                            actionHandled = true;
                            requiresRuntimeReconcile = true;
                        }
                        break;

                    case UndoActionType.AssetVersionRestored:
                        if (action.AssetId != null && action.PreviousMembership != null)
                        {
                            var asset = configuration.Assets.FirstOrDefault(a => a.Id == action.AssetId);
                            if (asset != null)
                            {
                                var currentMembership = asset.Addons.ToList();
                                var currentVersion = asset.CurrentVersion;
                                rollbackMutation = () =>
                                {
                                    asset.Addons = currentMembership;
                                    asset.CurrentVersion = currentVersion;
                                };
                                asset.Addons = action.PreviousMembership
                                    .Distinct(StringComparer.Ordinal)
                                    .ToList();
                                if (action.PreviousCurrentVersion.HasValue)
                                {
                                    asset.CurrentVersion =
                                        action.PreviousCurrentVersion.Value;
                                }
                                actionHandled = true;
                                requiresRuntimeReconcile = true;
                            }
                        }
                        break;
                }

                if (!actionHandled)
                {
                    throw new InvalidOperationException(
                        $"Undo target is no longer available for action {action.Id}.");
                }

                await SaveConfigurationImmediatelyAsync();
                configurationPersisted = true;

                if (requiresRuntimeReconcile)
                {
                    try
                    {
                        UpdateAddonStates();
                    }
                    catch (Exception ex)
                    {
                        // The desired state is already durable. Runtime failure follows
                        // the same latest-full-reconcile contract as other mutations.
                        QueueRuntimeApplyProvider?.Invoke();
                        errorHandler.HandleError(
                            ex,
                            "Undo was saved; runtime reconciliation was queued",
                            ErrorSeverity.Warning);
                    }
                }
                
                success = true;
                return true;
            }
            catch (Exception ex)
            {
                if (configurationPersisted)
                {
                    QueueRuntimeApplyProvider?.Invoke();
                    errorHandler.HandleError(
                        ex,
                        "Undo was saved; runtime reconciliation was queued",
                        ErrorSeverity.Warning);
                    success = true;
                    return true;
                }

                try
                {
                    rollbackMutation?.Invoke();
                    configuration.LastUpdated = previousLastUpdated;
                }
                catch (Exception rollbackException)
                {
                    errorHandler.HandleError(
                        rollbackException,
                        "Failed to roll back an unsuccessful Undo mutation",
                        ErrorSeverity.Error);
                }

                undoManager.RecordAction(action);

                if (FindStrictLinkModeException(ex) is StrictLinkModeException strict)
                {
                    strictFailure = strict;
                    errorCode = "strict_link_copy_blocked";
                }
                else
                {
                    errorCode = "undo_failed";
                }

                return false;
            }
            finally
            {
                stopwatch.Stop();
                var afterSnapshot = CaptureState();
                var afterHash = ComputeStateHash(afterSnapshot);

                LogExperimentEvent(
                    "UndoEnd",
                    eventScope: "system",
                    targetId: action.Id,
                    result: success ? "success" : "fail",
                    durationMs: stopwatch.ElapsedMilliseconds,
                    beforeHash: beforeHash,
                    afterHash: afterHash,
                    errorCode: errorCode,
                    operationId: operationId);

                if (strictFailure != null)
                {
                    throw strictFailure;
                }
            }
        }
        
            // 実体フォルダが存在する場合、管理フォルダに移動してからジャンクション作成
        // Initialize WorkshopIconResolver
        private void EnsureJunctionAssetExists()
        {
            if (DisableMode == DisableMode.Soft)
            {
                return;
            }

            var junctionAsset = configuration.Assets.FirstOrDefault(a => a.Id == "junction-system-asset");
            if (junctionAsset == null)
            {
                junctionAsset = new Asset("ジャンクション", true);
                junctionAsset.Id = "junction-system-asset";
                junctionAsset.Enabled = false;
                configuration.Assets.Add(junctionAsset);
            }
        }
        
        /// <summary>
        // Initialize WorkshopIconResolver
        /// </summary>
        public async Task UpdateJunctionAssetAsync()
        {
            await Task.CompletedTask;
            if (DisableMode == DisableMode.Soft)
            {
                return;
            }

            try
            {
                var junctionAsset = configuration.Assets.FirstOrDefault(a => a.Id == "junction-system-asset");
                if (junctionAsset == null) return;
                
                // Initialize WorkshopIconResolver
                var allAddons = configuration.AddonMetadata;
                var orphanedAddons = new List<string>();
                
                // Initialize WorkshopIconResolver
                var addonsInOtherAssets = new HashSet<string>();
                foreach (var asset in configuration.Assets)
                {
                    if (asset.Id != "junction-system-asset")
                    {
                        if (asset.ContainsAllAddons())
                        {
                            // Initialize WorkshopIconResolver
                            addonsInOtherAssets.UnionWith(allAddons
                                .Where(kvp => !kvp.Value.IsLocal)
                                .Select(kvp => kvp.Key));
                        }
                        else
                        {
                            addonsInOtherAssets.UnionWith(asset.Addons.Where(id => !IsLocalAddonId(id)));
                        }
                    }
                }
                
                // Initialize WorkshopIconResolver
                foreach (var addon in allAddons)
                {
                    if (addon.Value.IsLocal)
                    {
                        continue;
                    }

                    if (!addonsInOtherAssets.Contains(addon.Key))
                    {
                        var addonPath = Path.Combine(workshopPath, addon.Key);
                        var sourcePath = Path.Combine(addonsPath, addon.Key);
                        
                        // Initialize WorkshopIconResolver
                        bool addonExists = false;
                        
                        // Check if it's a GMA file addon
                        if (addon.Value.IsGmaFile)
                        {
                            string gmaCachePath = Path.Combine(gmodCachePath ?? "", addon.Key + ".gma");
                            string gmaSourcePath = Path.Combine(addonsPath, addon.Key + ".gma");
                            
        /// ディレクトリをマージする
                            addonExists = File.Exists(gmaSourcePath) || File.Exists(gmaCachePath);
                            
                            // GMA file exists in managed folder = disabled
                            if (addonExists && File.Exists(gmaSourcePath) && !File.Exists(gmaCachePath))
                            {
                                orphanedAddons.Add(addon.Key);
                            }
                        }
                        else
                        {
                            // Initialize WorkshopIconResolver
                            addonExists = Directory.Exists(sourcePath) || 
                                        (Directory.Exists(addonPath) && !junctionService.IsJunction(addonPath));
                            
                            if (addonExists && Directory.Exists(sourcePath))
                            {
                                // Initialize WorkshopIconResolver
                                bool hasJunction = Directory.Exists(addonPath) && junctionService.IsJunction(addonPath);
                                
                                // Initialize WorkshopIconResolver
                                if (!hasJunction)
                                {
                                    orphanedAddons.Add(addon.Key);
                                }
                            }
                        }
                        
                        // Initialize WorkshopIconResolver
                        if (!addonExists)
                        {
                            continue;
                        }
                    }
                }
                
            // RestoreOriginalStateAsyncを使用して、すべてのアドオンを元の状態に戻す
                foreach (var addonId in orphanedAddons)
                {
            // 設定を再初期化
                    if (addonId == "*")
                    {
                        errorHandler.HandleWarning("Skipping wildcard '*' addon", "UpdateJunctionAsset");
                        continue;
                    }
                    
                    if (!junctionAsset.Addons.Contains(addonId))
                    {
                        junctionAsset.AddAddon(addonId, AddonState.Disabled);
                    }
                }
                
                // Initialize WorkshopIconResolver
                var toRemove = new List<string>();
                foreach (var addonId in junctionAsset.Addons)
                {
                    // Initialize WorkshopIconResolver
                    if (addonId == "*")
                    {
                        errorHandler.HandleWarning("Removing wildcard '*' from junction asset", "UpdateJunctionAsset");
                        toRemove.Add(addonId);
                        continue;
                    }
                    
                    // Initialize WorkshopIconResolver
                    // Initialize WorkshopIconResolver
                    if (addonsInOtherAssets.Contains(addonId) && 
                        !orphanedAddons.Contains(addonId) && 
                        !junctionAsset.AddonStates.ContainsKey(addonId))
                    {
                        toRemove.Add(addonId);
                    }
                }
                
                foreach (var addonId in toRemove)
                {
                    junctionAsset.RemoveAddon(addonId);
                }
                
            }
            catch (Exception ex)
            {
                errorHandler.HandleError(ex, "Failed to update junction asset", ErrorSeverity.Warning);
            }
        }
        
        /// <summary>
        // Initialize WorkshopIconResolver
        public void RestoreAddonFromJunction(string addonId)
        {
            // Initialize WorkshopIconResolver
            var junctionAsset = configuration.Assets.FirstOrDefault(a => a.Id == "junction-system-asset");
            if (junctionAsset != null)
            {
                junctionAsset.RemoveAddon(addonId);
            }
            
            // Initialize WorkshopIconResolver
            if (configuration.JunctionHistory.TryGetValue(addonId, out var sourceAssetIds))
            {
                foreach (var assetId in sourceAssetIds)
                {
                    var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId);
                    if (asset != null)
                    {
                        if (asset.ContainsAllAddons())
                        {
                            // Initialize WorkshopIconResolver
                            asset.AddonStates.Remove(addonId);
                        }
                        else
                        {
                            asset.AddAddon(addonId, AddonState.Enabled);
                        }
                    }
                }
                
                // Initialize WorkshopIconResolver
                configuration.JunctionHistory.Remove(addonId);
            }
            
            UpdateAddonStates();
        }
        
        /// <summary>
        /// 既存のメタデータを修復する
        public List<string> GetAddonSourceAssets(string addonId)
        {
            if (configuration.JunctionHistory.TryGetValue(addonId, out var sourceAssetIds))
            {
                return new List<string>(sourceAssetIds);
            }
            return new List<string>();
        }
        
        /// <summary>
        // Initialize WorkshopIconResolver
        private void HandleExistingDirectoryDuringMigration(string directory, string targetPath, string dirName)
        {
            // Initialize WorkshopIconResolver
            errorHandler.HandleWarning($"Found real folder instead of junction for addon {dirName} during migration. Converting to managed addon.", "MigrateExistingAddons");
            
            string tempPath = directory + "_temp_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Directory.Move(directory, tempPath);
            bool movedToTarget = false;
            
            try
            {
                if (!Directory.Exists(targetPath))
                {
                    Directory.Move(tempPath, targetPath);
                    movedToTarget = true;
                }

                bool workshopPresenceCreated = false;
                string gmaPath = Path.Combine(targetPath, $"{dirName}.gma");

                if (File.Exists(gmaPath))
                {
                    try
                    {
                        junctionService.CreateWorkshopAddonStructure(workshopPath, dirName, gmaPath);
                        workshopPresenceCreated = true;
                    }
                    catch (Exception ex)
                    {
                        errorHandler.HandleError(ex,
                            $"Failed to create hard link structure for addon {dirName}, falling back to junction",
                            ErrorSeverity.Warning);
                    }
                }

                if (!workshopPresenceCreated)
                {
                    CreateJunctionWithMetrics(directory, targetPath);
                    workshopPresenceCreated = true;
                }

                if (!movedToTarget && Directory.Exists(tempPath))
                {
                    try
                    {
                        MergeDirectories(tempPath, targetPath);
                        Directory.Delete(tempPath, true);
                    }
                    catch (Exception ex)
                    {
                        // Initialize WorkshopIconResolver
                        errorHandler.HandleError(ex,
                            $"Failed to merge addon {dirName} contents into managed folder. Leaving temp folder: {tempPath}",
                            ErrorSeverity.Warning);
                    }
                }
            }
            catch
            {
                // Initialize WorkshopIconResolver
                EnsureWorkshopPathAvailableForRestore(directory);

                if (Directory.Exists(tempPath) && !Directory.Exists(directory))
                {
                    Directory.Move(tempPath, directory);
                }
                else if (movedToTarget && Directory.Exists(targetPath) && !Directory.Exists(directory))
                {
                    Directory.Move(targetPath, directory);
                }
                throw;
            }
        }

        /// <summary>
        // Initialize WorkshopIconResolver
        /// </summary>
        private void MergeDirectories(string source, string destination)
        {
            ValidatePath(source, "source");
            ValidatePath(destination, "destination");
            
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                // Initialize WorkshopIconResolver
                string relativePath = file.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string destFile = Path.Combine(destination, relativePath);
                ValidatePath(destFile, "destFile");
                
                Directory.CreateDirectory(Path.GetDirectoryName(destFile));
                
                if (File.Exists(destFile))
                {
                    File.Delete(destFile);
                }
                
                File.Move(file, destFile);
            }
        }
        
        public async Task ResetManagerAsync()
        {
            await _runtimeReconcileGate.WaitAsync();
            try
            {
            errorHandler.HandleInfo("Starting GAM configuration reset", "ResetManager");

            var originalAssets = configuration.Assets;
            var existingSubscribeAsset = configuration.Assets
                .FirstOrDefault(asset => asset.Id == SubscribeSystemAssetId);
            var originalSubscribeName = existingSubscribeAsset?.Name;
            var originalSubscribeIsSystem = existingSubscribeAsset?.IsSystem;
            var originalSubscribeFavorite = existingSubscribeAsset?.IsFavorite;
            var originalSubscribeImagePath = existingSubscribeAsset?.ImagePath;
            var originalSubscribeState = existingSubscribeAsset?.GetWholeState();
            var originalSubscribeAddons = existingSubscribeAsset?.Addons.ToList();
            var originalSubscribeAddonStates = existingSubscribeAsset == null
                ? null
                : new Dictionary<string, AddonState>(existingSubscribeAsset.AddonStates);
            var originalSubscribeVersions = existingSubscribeAsset?.VersionHistory.ToList();
            var originalSubscribeCurrentVersion = existingSubscribeAsset?.CurrentVersion;
            var originalInitialImportCompleted =
                configuration.InitialRuntimeImportCompleted;
            var originalInitialImportCompletedAtUtc =
                configuration.InitialRuntimeImportCompletedAtUtc;
            var originalGamAppliedBaselineInitialized =
                configuration.GamAppliedRuntimeBaselineInitialized;
            var originalGamAppliedStates = new Dictionary<string, bool>(
                configuration.LastGamAppliedAddonStates,
                StringComparer.Ordinal);
            var originalGamAppliedAtUtc = configuration.LastGamAppliedRuntimeAtUtc;
            var originalGamAppliedStateStorePath =
                configuration.LastGamAppliedStateStorePath;
            var originalObservationBaselineInitialized =
                configuration.GmodObservationBaselineInitialized;
            var originalObservedStates = new Dictionary<string, bool>(
                configuration.LastObservedGmodAddonStates,
                StringComparer.Ordinal);
            var originalObservedAtUtc = configuration.LastObservedGmodRuntimeAtUtc;
            var originalObservedStateStorePath =
                configuration.LastObservedGmodStateStorePath;
            var originalPendingGamRuntimeWrite =
                configuration.PendingGamRuntimeWrite;
            var originalAttributionMigrationPending =
                configuration.GmodAttributionMigrationPending;
            var originalAddonFavorites = configuration.AddonMetadata.ToDictionary(
                entry => entry.Key,
                entry => entry.Value.IsFavorite,
                StringComparer.Ordinal);
            var subscribeAsset = existingSubscribeAsset;
            if (subscribeAsset == null)
            {
                subscribeAsset = new Asset("Subscribe Asset", true)
                {
                    Id = SubscribeSystemAssetId
                };
            }

            subscribeAsset.Name = "Subscribe Asset";
            subscribeAsset.IsSystem = true;
            subscribeAsset.IsFavorite = false;
            subscribeAsset.ImagePath = null;
            subscribeAsset.VersionHistory.Clear();
            subscribeAsset.CurrentVersion = 0;
            subscribeAsset.SetAllAddons();
            subscribeAsset.SetWholeState(AddonState.Enabled);

            var gmodDisabledAsset = new Asset(
                SystemAssetDefinitions.GmodDisabledName,
                isSystem: true)
            {
                Id = SystemAssetDefinitions.GmodDisabledId
            };
            gmodDisabledAsset.SetWholeState(AddonState.Excluded);

            configuration.Assets = new List<Asset>
            {
                subscribeAsset,
                gmodDisabledAsset
            };
            configuration.InitialRuntimeImportCompleted = true;
            configuration.InitialRuntimeImportCompletedAtUtc = DateTime.UtcNow;
            configuration.GamAppliedRuntimeBaselineInitialized = false;
            configuration.LastGamAppliedAddonStates.Clear();
            configuration.LastGamAppliedRuntimeAtUtc = null;
            configuration.LastGamAppliedStateStorePath = null;
            configuration.PendingGamRuntimeWrite = null;
            configuration.GmodAttributionMigrationPending = false;

            // Reset intentionally discards prior external deltas. A valid current
            // snapshot is acknowledged before a possible GMod-running defer, so
            // the old OFF state cannot be re-imported into the freshly empty Asset.
            if (TryCaptureCurrentSubscribedRuntimeStates(out var resetActualStates))
            {
                configuration.GmodObservationBaselineInitialized = true;
                configuration.LastObservedGmodAddonStates = resetActualStates;
                configuration.LastObservedGmodRuntimeAtUtc = DateTime.UtcNow;
                configuration.LastObservedGmodStateStorePath =
                    gmodAddonStateStore?.NoMountFilePath;
            }
            else
            {
                configuration.GmodObservationBaselineInitialized = false;
                configuration.LastObservedGmodAddonStates.Clear();
                configuration.LastObservedGmodRuntimeAtUtc = null;
                configuration.LastObservedGmodStateStorePath = null;
            }

            foreach (var addon in configuration.AddonMetadata.Values)
            {
                addon.IsFavorite = false;
            }

            try
            {
                await SaveConfigurationImmediatelyAsync();
            }
            catch
            {
                configuration.Assets = originalAssets;
                if (existingSubscribeAsset != null)
                {
                    existingSubscribeAsset.Name = originalSubscribeName!;
                    existingSubscribeAsset.IsSystem = originalSubscribeIsSystem!.Value;
                    existingSubscribeAsset.IsFavorite = originalSubscribeFavorite!.Value;
                    existingSubscribeAsset.ImagePath = originalSubscribeImagePath;
                    existingSubscribeAsset.SetWholeState(
                        originalSubscribeState!.Value);
                    existingSubscribeAsset.Addons = originalSubscribeAddons!;
                    existingSubscribeAsset.AddonStates = originalSubscribeAddonStates!;
                    existingSubscribeAsset.VersionHistory = originalSubscribeVersions!;
                    existingSubscribeAsset.CurrentVersion =
                        originalSubscribeCurrentVersion!.Value;
                }
                configuration.InitialRuntimeImportCompleted =
                    originalInitialImportCompleted;
                configuration.InitialRuntimeImportCompletedAtUtc =
                    originalInitialImportCompletedAtUtc;
                configuration.GamAppliedRuntimeBaselineInitialized =
                    originalGamAppliedBaselineInitialized;
                configuration.LastGamAppliedAddonStates = originalGamAppliedStates;
                configuration.LastGamAppliedRuntimeAtUtc = originalGamAppliedAtUtc;
                configuration.LastGamAppliedStateStorePath =
                    originalGamAppliedStateStorePath;
                configuration.GmodObservationBaselineInitialized =
                    originalObservationBaselineInitialized;
                configuration.LastObservedGmodAddonStates = originalObservedStates;
                configuration.LastObservedGmodRuntimeAtUtc = originalObservedAtUtc;
                configuration.LastObservedGmodStateStorePath =
                    originalObservedStateStorePath;
                configuration.PendingGamRuntimeWrite = originalPendingGamRuntimeWrite;
                configuration.GmodAttributionMigrationPending =
                    originalAttributionMigrationPending;
                foreach (var favorite in originalAddonFavorites)
                {
                    if (configuration.AddonMetadata.TryGetValue(
                            favorite.Key,
                            out var addon))
                    {
                        addon.IsFavorite = favorite.Value;
                    }
                }
                throw;
            }

            undoManager.Clear();
            try
            {
                if (!DeferRuntimeApplyIfGmodRunning())
                {
                    var resetResult = await UpdateAddonStatesCoreAsync(
                        logEvents: true,
                        parentOperationId: null,
                        progress: null,
                        observeExternalChanges: false);
                    if (!resetResult.Succeeded)
                    {
                        QueueRuntimeApplyProvider?.Invoke();
                    }
                }
            }
            catch (Exception ex)
            {
                QueueRuntimeApplyProvider?.Invoke();
                errorHandler.HandleError(
                    ex,
                    "GAM reset was saved; runtime reconciliation was queued",
                    ErrorSeverity.Warning);
            }
            errorHandler.HandleInfo("GAM configuration reset completed", "ResetManager");
            }
            finally
            {
                _runtimeReconcileGate.Release();
            }
        }
        
        public async Task RestoreOriginalStateAsync()
        {
            errorHandler.HandleInfo("Starting RestoreOriginalStateAsync", "RestoreOriginalState");
            
            // Initialize WorkshopIconResolver
            await RemoveAllJunctionsAndHardLinksAsync();
            
            // Initialize WorkshopIconResolver
            await RestoreManagedAddonsAsync();

            var localRestored = await RestoreManagedLocalAddonsAsync();
            
            // Initialize WorkshopIconResolver
            if (localRestored)
            {
                await CleanupManagerDirectoriesAsync();
            }
            else
            {
                errorHandler.HandleWarning(
                    "Local addon data was not fully restored; skipped manager cleanup to avoid data loss.",
                    "RestoreOriginalState");
            }
            
        /// </summary>
            if (File.Exists(configPath))
            {
                ValidatePath(configPath, "configPath");
                File.Delete(configPath);
            }
            
            if (File.Exists(pendingPath))
            {
                ValidatePath(pendingPath, "pendingPath");
                File.Delete(pendingPath);
            }
            
            errorHandler.HandleInfo("RestoreOriginalStateAsync completed successfully", "RestoreOriginalState");
        }
        
        private async Task RemoveAllJunctionsAndHardLinksAsync()
        {
            await Task.CompletedTask;
            errorHandler.HandleInfo("Removing all junctions and hard links", "RemoveAllJunctionsAndHardLinks");
            
                    // 操作をクリーンアップ（必要に応じて復旧処理を追加）
            foreach (var entry in Directory.GetDirectories(workshopPath))
            {
                var dirName = Path.GetFileName(entry);
                
                try
                {
        /// システムの整合性をチェックして修復
                    if (junctionService.IsJunction(entry))
                    {
                        errorHandler.HandleInfo($"Removing junction: {dirName}", "RemoveAllJunctionsAndHardLinks");
                        junctionService.RemoveJunction(entry);
                    }
                    // Initialize WorkshopIconResolver
                    {
                        var gmaPath = Path.Combine(entry, $"{dirName}.gma");
                        if (File.Exists(gmaPath))
                        {
                            // Initialize WorkshopIconResolver
                            var managedGmaPath = Path.Combine(addonsPath, dirName, $"{dirName}.gma");
                            if (File.Exists(managedGmaPath) && junctionService.IsHardLink(gmaPath, managedGmaPath))
                            {
                                errorHandler.HandleInfo($"Removing hard link: {gmaPath}", "RemoveAllJunctionsAndHardLinks");
                                junctionService.RemoveHardLink(gmaPath);
                                // Initialize WorkshopIconResolver
                                if (!Directory.GetFileSystemEntries(entry).Any())
                                {
                                    Directory.Delete(entry);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    errorHandler.HandleError(ex, $"Failed to process directory: {dirName}", ErrorSeverity.Warning);
                }
            }
            
            // Initialize WorkshopIconResolver
            if (!string.IsNullOrEmpty(gmodCachePath) && Directory.Exists(gmodCachePath))
            {
                foreach (var gmaFile in Directory.GetFiles(gmodCachePath, "*.gma"))
                {
                    var fileName = Path.GetFileName(gmaFile);
                    var addonId = Path.GetFileNameWithoutExtension(fileName);
                    var managedGmaPath = Path.Combine(gmodCacheAddonsPath ?? "", $"{addonId}.gma");
                    
                    if (File.Exists(managedGmaPath) && junctionService.IsHardLink(gmaFile, managedGmaPath))
                    {
                        try
                        {
                            errorHandler.HandleInfo($"Removing cache hard link: {fileName}", "RemoveAllJunctionsAndHardLinks");
                            junctionService.RemoveHardLink(gmaFile);
                        }
                        catch (Exception ex)
                        {
                            errorHandler.HandleError(ex, $"Failed to remove cache hard link: {fileName}", ErrorSeverity.Warning);
                        }
                    }
                }
            }
        }
        
        private async Task RestoreManagedAddonsAsync()
        {
            await Task.CompletedTask;
            errorHandler.HandleInfo("Restoring managed addons to original locations", "RestoreManagedAddons");
            
            // Initialize WorkshopIconResolver
            if (Directory.Exists(addonsPath))
            {
                foreach (var managedAddonPath in Directory.GetDirectories(addonsPath))
                {
                    var addonId = Path.GetFileName(managedAddonPath);
                    var originalPath = Path.Combine(workshopPath, addonId);
                    ValidatePath(managedAddonPath, "managedAddonPath");
                    ValidatePath(originalPath, "originalPath");
                    
                    try
                    {
                        // Initialize WorkshopIconResolver
                        if (!Directory.Exists(originalPath))
                        {
                            errorHandler.HandleInfo($"Moving addon {addonId} back to workshop", "RestoreManagedAddons");
                            Directory.Move(managedAddonPath, originalPath);
                        }
                        else
                        {
                            // Initialize WorkshopIconResolver
                            errorHandler.HandleInfo($"Merging addon {addonId} back to workshop", "RestoreManagedAddons");
                            MergeDirectories(managedAddonPath, originalPath);
                            Directory.Delete(managedAddonPath, true);
                        }
                    }
                    catch (Exception ex)
                    {
                        errorHandler.HandleError(ex, $"Failed to restore addon: {addonId}", ErrorSeverity.Warning);
                    }
                }
            }
            
            // Initialize WorkshopIconResolver
            if (!string.IsNullOrEmpty(gmodCacheAddonsPath) && Directory.Exists(gmodCacheAddonsPath))
            {
                foreach (var gmaFile in Directory.GetFiles(gmodCacheAddonsPath, "*.gma"))
                {
                    var fileName = Path.GetFileName(gmaFile);
                    var originalPath = Path.Combine(gmodCachePath, fileName);
                    ValidatePath(gmaFile, "gmaFile");
                    ValidatePath(originalPath, "originalPath");
                    
                    try
                    {
                        if (!File.Exists(originalPath))
                        {
                            errorHandler.HandleInfo($"Moving cache GMA {fileName} back", "RestoreManagedAddons");
                            File.Move(gmaFile, originalPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        errorHandler.HandleError(ex, $"Failed to restore cache GMA: {fileName}", ErrorSeverity.Warning);
                    }
                }
            }
        }

        private Task<bool> RestoreManagedLocalAddonsAsync()
        {
            if (string.IsNullOrWhiteSpace(localManagedRootPath) || !Directory.Exists(localManagedRootPath))
            {
                return Task.FromResult(true);
            }

            var localAddons = configuration?.AddonMetadata?.Values
                .Where(addon => addon.IsLocal)
                .ToList() ?? new List<WorkshopAddon>();

            bool success = true;

            foreach (var addon in localAddons)
            {
                try
                {
                    RestoreManagedLocalAddon(addon);
                }
                catch (Exception ex)
                {
                    success = false;
                    errorHandler.HandleError(ex, $"Failed to restore local addon {addon.Id}", ErrorSeverity.Warning);
                }
            }

            try
            {
                if (Directory.Exists(localManagedRootPath) &&
                    Directory.EnumerateFileSystemEntries(localManagedRootPath).Any())
                {
                    success = false;
                }
            }
            catch (Exception ex)
            {
                success = false;
                errorHandler.HandleWarning($"Failed to inspect local addon manager directory: {ex.Message}", "RestoreLocalAddons");
            }

            if (success)
            {
                try
                {
                    ValidatePath(localManagedRootPath, "localManagedRootPath");
                    Directory.Delete(localManagedRootPath, true);
                }
                catch (Exception ex)
                {
                    errorHandler.HandleWarning($"Failed to delete local addon manager directory: {ex.Message}", "RestoreLocalAddons");
                }
            }

            return Task.FromResult(success);
        }

        private void RestoreManagedLocalAddon(WorkshopAddon addon)
        {
            if (addon == null || !addon.IsLocal)
            {
                return;
            }

            var mountPath = ResolveLocalMountPath(addon);
            if (string.IsNullOrWhiteSpace(mountPath))
            {
                throw new InvalidOperationException($"Local addon {addon.Id} has no mount path.");
            }

            mountPath = NormalizeLocalPath(mountPath);
            var managedPath = addon.LocalManagedPath ?? GetDefaultLocalManagedPath(addon.Id, addon.IsGmaFile);
            if (string.IsNullOrWhiteSpace(managedPath))
            {
                throw new InvalidOperationException($"Local addon {addon.Id} has no managed path.");
            }

            if (addon.IsGmaFile)
            {
                RestoreManagedLocalGma(addon.Id, managedPath, mountPath);
            }
            else
            {
                RestoreManagedLocalFolder(addon.Id, managedPath, mountPath);
            }
        }

        private void RestoreManagedLocalFolder(string addonId, string managedPath, string mountPath)
        {
            if (!Directory.Exists(managedPath))
            {
                return;
            }

            EnsureLocalMountParentDirectory(mountPath);
            ValidatePath(managedPath, "localManagedPath");
            ValidatePath(mountPath, "localMountPath");

            if (Directory.Exists(mountPath))
            {
                if (junctionService.IsJunction(mountPath))
                {
                    junctionService.RemoveJunction(mountPath);
                }

                if (Directory.Exists(mountPath))
                {
                    MergeDirectories(managedPath, mountPath);
                    Directory.Delete(managedPath, true);
                    return;
                }
            }

            MoveDirectoryWithFallback(addonId, managedPath, mountPath, "RestoreLocalFolder");
        }

        private void RestoreManagedLocalGma(string addonId, string managedPath, string mountPath)
        {
            if (!File.Exists(managedPath))
            {
                return;
            }

            EnsureLocalMountParentDirectory(mountPath);
            ValidatePath(managedPath, "localManagedPath");
            ValidatePath(mountPath, "localMountPath");

            if (File.Exists(mountPath))
            {
                if (IsHardLink(mountPath, managedPath))
                {
                    File.Delete(mountPath);
                    MoveFileWithFallback(addonId, managedPath, mountPath, "RestoreLocalGma");
                    return;
                }

                var backupPath = GetAvailableBackupPath(mountPath, "gam-backup");
                CopyFileForLinkFallback(addonId, managedPath, backupPath, "RestoreLocalGmaBackup", overwrite: false);
                File.Delete(managedPath);
                errorHandler.HandleWarning(
                    $"Local addon {addonId} already exists at mount path; saved managed copy to {Path.GetFileName(backupPath)}.",
                    "RestoreLocalAddons");
                return;
            }

            MoveFileWithFallback(addonId, managedPath, mountPath, "RestoreLocalGma");
        }

        private static string GetAvailableBackupPath(string originalPath, string suffix)
        {
            var candidate = $"{originalPath}.{suffix}";
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            for (int i = 1; i <= 50; i++)
            {
                var indexed = $"{originalPath}.{suffix}.{i}";
                if (!File.Exists(indexed))
                {
                    return indexed;
                }
            }

            return $"{originalPath}.{suffix}.{Guid.NewGuid().ToString("N").Substring(0, 6)}";
        }
        
        private async Task CleanupManagerDirectoriesAsync()
        {
            await Task.CompletedTask;
            errorHandler.HandleInfo("Cleaning up manager directories", "CleanupManagerDirectories");
            
            // Initialize WorkshopIconResolver
            if (Directory.Exists(managerPath))
            {
                try
                {
                    ValidatePath(managerPath, "managerPath");
                    Directory.Delete(managerPath, true);
                    errorHandler.HandleInfo("Deleted manager directory", "CleanupManagerDirectories");
                }
                catch (Exception ex)
                {
                    errorHandler.HandleError(ex, "Failed to delete manager directory", ErrorSeverity.Warning);
                }
            }
            
            // Initialize WorkshopIconResolver
            if (!string.IsNullOrEmpty(gmodCacheManagerPath) && Directory.Exists(gmodCacheManagerPath))
            {
                try
                {
                    ValidatePath(gmodCacheManagerPath, "gmodCacheManagerPath");
                    Directory.Delete(gmodCacheManagerPath, true);
                    errorHandler.HandleInfo("Deleted cache manager directory", "CleanupManagerDirectories");
                }
                catch (Exception ex)
                {
                    errorHandler.HandleError(ex, "Failed to delete cache manager directory", ErrorSeverity.Warning);
                }
            }
        }
        
        private async Task DisableAllAddonsAsync()
        {
            await Task.CompletedTask;
            var allAddons = configuration.AddonMetadata.Keys.ToList();
            foreach (var addonId in allAddons)
            {
                try
                {
                    var addon = configuration.AddonMetadata[addonId];
                    if (addon.IsEnabled)
                    {
                        if (addon.IsGmaFile)
                        {
                            DisableAddon(addonId);
                        }
                        else
                        {
                            DisableAddon(addonId);
                        }
                    }
                }
                catch
                {
                    // Initialize WorkshopIconResolver
                    }
            }
        }
        
        /// <summary>
        // Initialize WorkshopIconResolver
        /// </summary>
        /// <summary>
        // Initialize WorkshopIconResolver
        private async Task CheckIncompleteOperationsAsync()
        {
            await Task.CompletedTask;
            var incompleteLogs = operationLogManager.GetIncompleteLogs();
            if (incompleteLogs.Count > 0)
            {
                errorHandler.HandleWarning($"Found {incompleteLogs.Count} incomplete operations from previous session", "CheckIncompleteOperations");
                
                // Initialize WorkshopIconResolver
                foreach (var log in incompleteLogs)
                {
                    errorHandler.HandleWarning(
                        $"Incomplete {log.Type} operation from {log.StartTime:yyyy-MM-dd HH:mm:ss} with {log.Items.Count} items",
                        "IncompleteOperation"
                    );
                    
                    // Initialize WorkshopIconResolver
                    operationLogManager.RemoveLog(log.Id);
                }
            }
        }
        
        /// <summary>
        // Initialize WorkshopIconResolver
        /// </summary>
	        private async Task ValidateSystemIntegrityAsync()
	        {
	            errorHandler.HandleInfo("Starting system integrity check...", "ValidateSystemIntegrity");
            
            var repairCount = 0;
            
            // Initialize WorkshopIconResolver
            var backupPath = configPath + ".bak";
            if (!File.Exists(backupPath) && File.Exists(configPath))
            {
                try
                {
                    File.Copy(configPath, backupPath, true);
                    errorHandler.HandleInfo("Created configuration backup", "ValidateSystemIntegrity");
                }
                catch (Exception)
                {
                    // Failed to create backup - log but continue validation
                    errorHandler.HandleWarning($"Failed to create configuration backup", "ValidateAndRepairConfiguration");
                }
            }
            
                // Delete existing link if present
	            foreach (var kvp in configuration.AddonMetadata.ToList())
	            {
	                var addon = kvp.Value;
	                var addonId = kvp.Key;
	                
	                try
	                {
	                    if (addon.IsGmaFile)
	                    {
	                        // Initialize WorkshopIconResolver
	                        if (!string.IsNullOrEmpty(gmodCachePath))
	                        {
	                            var cachePath = Path.Combine(gmodCachePath, addonId + ".gma");
	                            
	                            // Initialize WorkshopIconResolver
	                            var shouldBeEnabled = CalculateFinalAddonState(addonId);
	                            if (shouldBeEnabled && !File.Exists(cachePath))
	                            {
	                                var sourcePath = ResolveGmaSourcePath(addonId, addon);
	                                if (sourcePath != null)
	                                {
	                                    errorHandler.HandleWarning($"Repairing missing cache GMA for {addonId}", "ValidateSystemIntegrity");
	                                    EnsureCacheStructureForGma(addonId, sourcePath);
	                                    if (File.Exists(cachePath))
	                                    {
	                                        repairCount++;
	                                    }
	                                }
	                            }
	                        }
	                    }
	                    else
	                    {
	                        // Initialize WorkshopIconResolver
	                        var workshopAddonPath = Path.Combine(workshopPath, addonId);
	                        var managedAddonPath = Path.Combine(addonsPath, addonId);

	                        // Initialize WorkshopIconResolver
	                        var shouldBeEnabled = CalculateFinalAddonState(addonId);

	                        if (shouldBeEnabled)
	                        {
	                            // Initialize WorkshopIconResolver
	                            if (Directory.Exists(managedAddonPath))
	                            {
	                                if (!Directory.Exists(workshopAddonPath))
	                                {
	                                    errorHandler.HandleWarning($"Repairing missing workshop junction for {addonId}", "ValidateSystemIntegrity");
	                                    CreateJunctionWithMetrics(workshopAddonPath, managedAddonPath);
	                                    repairCount++;
	                                }
	                                else if (junctionService.IsJunction(workshopAddonPath))
	                                {
                    // 同じボリューム、同じFileIndexなら同一ファイル（ハードリンク）
	                                    CreateJunctionWithMetrics(workshopAddonPath, managedAddonPath);
	                                }
	                                else
	                                {
	                                    // Initialize WorkshopIconResolver
	                                    if (RemoveDisabledStub(workshopPath, addonId))
	                                    {
	                                        errorHandler.HandleWarning($"Repairing workshop stub for {addonId}", "ValidateSystemIntegrity");
	                                        CreateJunctionWithMetrics(workshopAddonPath, managedAddonPath);
	                                        repairCount++;
	                                    }
	                                    else
	                                    {
	                                        // Initialize WorkshopIconResolver
	                                        bool isEmpty = false;
	                                        try
	                                        {
	                                            isEmpty = !Directory.EnumerateFileSystemEntries(workshopAddonPath).Any();
	                                        }
	                                        catch
	                                        {
	                                            isEmpty = false;
	                                        }

	                                        if (isEmpty)
	                                        {
	                                            errorHandler.HandleWarning($"Repairing empty workshop folder for {addonId}", "ValidateSystemIntegrity");
	                                            Directory.Delete(workshopAddonPath, true);
	                                            CreateJunctionWithMetrics(workshopAddonPath, managedAddonPath);
	                                            repairCount++;
	                                        }
	                                        else
	                                        {
	                                            // Initialize WorkshopIconResolver
	                                            errorHandler.HandleWarning(
	                                                $"Workshop path for addon {addonId} exists but is not a junction; skipping automatic repair to avoid data loss.",
	                                                "ValidateSystemIntegrity");
	                                        }
	                                    }
	                                }
	                            }
	                        }
	                        else
	                        {
	                            // Initialize WorkshopIconResolver
	                            if (Directory.Exists(workshopAddonPath) && junctionService.IsJunction(workshopAddonPath))
	                            {
	                                errorHandler.HandleWarning($"Removing unexpected workshop junction for {addonId}", "ValidateSystemIntegrity");
	                                junctionService.RemoveJunction(workshopAddonPath);
	                                repairCount++;
	                            }
	                        }
	                    }
	                }
	                catch (Exception ex)
	                {
	                    errorHandler.HandleError(ex, $"Failed to validate addon {addonId}", ErrorSeverity.Warning);
                }
            }
            
            if (repairCount > 0)
            {
                errorHandler.HandleInfo($"System integrity check completed. Repaired {repairCount} issues.", "ValidateSystemIntegrity");
            }
            else
            {
                errorHandler.HandleInfo("System integrity check completed. No issues found.", "ValidateSystemIntegrity");
            }
            
            await Task.CompletedTask;
        }
        
        public void Dispose()
        {
            if (_sessionLogged != 0)
            {
                LogExperimentEvent("SessionEnd", eventScope: "system", result: "success");
            }

            lock (_saveLock)
            {
                _saveDebounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _saveRequested = false;
            }

            if (_initializationCompleted)
            {
                try
                {
                    // Always enqueue one final snapshot. The save gate places it after
                    // any debounce callback that already cleared _saveRequested but is
                    // still serializing or waiting to write.
                    var saveTask = Task.Run(() => SaveConfigurationInternalAsync());
                    if (!saveTask.Wait(DisposeSaveFlushTimeout))
                    {
                        errorHandler.HandleWarning(
                            $"Timed out while flushing configuration during dispose after {DisposeSaveFlushTimeout.TotalSeconds:0} seconds.",
                            "Dispose");
                    }
                }
                catch (Exception ex)
                {
                    errorHandler.HandleError(ex, "Dispose", ErrorSeverity.Warning);
                }
            }

            _saveDebounceTimer?.Dispose();
        }
        
        #region Hard Link Utilities
        
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
        
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFileAttributes(string lpFileName);
        
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);
        
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(
            IntPtr hFile,
            out BY_HANDLE_FILE_INFORMATION lpFileInformation);
        
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
        
        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }
        
        [StructLayout(LayoutKind.Sequential)]
        private struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public FILETIME CreationTime;
            public FILETIME LastAccessTime;
            public FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }
        
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        
        private bool CreateHardLinkSafe(string linkPath, string targetPath)
        {
            try
            {
                // Validate paths before proceeding
                ValidatePath(linkPath, "linkPath");
                ValidatePath(targetPath, "targetPath");
                
                // Ensure target exists
                if (!File.Exists(targetPath))
                    return false;
                    
                // Delete existing link if present
                if (File.Exists(linkPath))
                {
                    File.Delete(linkPath);
                }
                
        /// 重複アドオンをクリーンアップする（同じIDでディレクトリとGMAの両方が存在する場合）
                var created = CreateHardLink(linkPath, targetPath, IntPtr.Zero);
                if (created)
                {
                    linkMetricsContext.Value?.RecordHardlink();
                }
                return created;
            }
            catch (Exception ex)
            {
                errorHandler.HandleError(ex, $"Failed to create hard link: {linkPath} -> {targetPath}", ErrorSeverity.Warning);
                return false;
            }
        }
        
        private bool IsHardLink(string path1, string path2)
        {
            try
            {
                if (!File.Exists(path1) || !File.Exists(path2))
                    return false;

                IntPtr handle1 = IntPtr.Zero;
                IntPtr handle2 = IntPtr.Zero;
                
                try
                {
                    // Initialize WorkshopIconResolver
                    handle1 = CreateFile(path1, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
                    
                    if (handle1.ToInt64() == -1)
                        return false;
                        
                    handle2 = CreateFile(path2, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
                    
                    if (handle2.ToInt64() == -1)
                        return false;
                    
                    BY_HANDLE_FILE_INFORMATION info1, info2;
                    
                    if (!GetFileInformationByHandle(handle1, out info1) ||
                        !GetFileInformationByHandle(handle2, out info2))
                    {
                        return false;
                    }

                    // Initialize WorkshopIconResolver
                    return info1.VolumeSerialNumber == info2.VolumeSerialNumber &&
                           info1.FileIndexHigh == info2.FileIndexHigh &&
                           info1.FileIndexLow == info2.FileIndexLow;
                }
                finally
                {
                    if (handle1 != IntPtr.Zero && handle1.ToInt64() != -1)
                        CloseHandle(handle1);
                    if (handle2 != IntPtr.Zero && handle2.ToInt64() != -1)
                        CloseHandle(handle2);
                }
            }
            catch
            {
                return false;
            }
        }
        
        private bool AreSameDrive(string path1, string path2)
        {
            try
            {
                var root1 = Path.GetPathRoot(Path.GetFullPath(path1));
                var root2 = Path.GetPathRoot(Path.GetFullPath(path2));
                return string.Equals(root1, root2, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
        
        #endregion
        
        /// <summary>
        // Initialize WorkshopIconResolver
        /// </summary>
        public async Task RepairAddonMetadataAsync()
        {
            errorHandler.HandleInfo("Starting addon metadata repair...", "RepairAddonMetadata");
            
            bool metadataUpdated = false;
            
            foreach (var kvp in configuration.AddonMetadata.ToList())
            {
                var addonId = kvp.Key;
                var addon = kvp.Value;
                bool needsUpdate = false;
                
            // 設定を保存
                if (!string.IsNullOrEmpty(addon.FolderPath) && 
                    addon.FolderPath.EndsWith(".gma") && 
                    (addon.FolderPath.Contains(gmodCachePath) || addon.FolderPath.Contains(gmodCacheAddonsPath)))
                {
                    if (!addon.IsGmaFile)
                    {
                        addon.IsGmaFile = true;
                        needsUpdate = true;
                        errorHandler.HandleInfo($"Fixed IsGmaFile flag for {addonId}", "RepairAddonMetadata");
                    }
                }
                
                // Initialize WorkshopIconResolver
                if (!addon.IsGmaFile && !string.IsNullOrEmpty(addon.FolderPath) && !addon.FolderPath.EndsWith(".gma"))
                {
                    // Initialize WorkshopIconResolver
                    string? gmaPath = null;
                    
                    if (!string.IsNullOrEmpty(gmodCacheAddonsPath))
                    {
                        gmaPath = Path.Combine(gmodCacheAddonsPath, $"{addonId}.gma");
                        if (!File.Exists(gmaPath))
                        {
                            gmaPath = null;
                        }
                    }
                    
                    if (gmaPath == null && !string.IsNullOrEmpty(gmodCachePath))
                    {
                        gmaPath = Path.Combine(gmodCachePath, $"{addonId}.gma");
                        if (!File.Exists(gmaPath))
                        {
                            gmaPath = null;
                        }
                    }
                    
                    if (gmaPath != null)
                    {
                        // Initialize WorkshopIconResolver
                        addon.FolderPath = gmaPath;
                        addon.IsGmaFile = true;
                        needsUpdate = true;
                        errorHandler.HandleInfo($"Fixed path and IsGmaFile flag for {addonId}", "RepairAddonMetadata");
                    }
                }
                
                // Initialize WorkshopIconResolver
                if (addon.IsGmaFile)
                {
                    // Initialize WorkshopIconResolver
                    string? correctGmaPath = null;
                    
                    if (!string.IsNullOrEmpty(gmodCacheAddonsPath))
                    {
                        correctGmaPath = Path.Combine(gmodCacheAddonsPath, $"{addonId}.gma");
                        if (!File.Exists(correctGmaPath))
                        {
                            correctGmaPath = null;
                        }
                    }
                    
                    if (correctGmaPath == null && !string.IsNullOrEmpty(gmodCachePath))
                    {
                        correctGmaPath = Path.Combine(gmodCachePath, $"{addonId}.gma");
                        if (!File.Exists(correctGmaPath))
                        {
                            correctGmaPath = null;
                        }
                    }
                    
                    if (correctGmaPath != null && addon.FolderPath != correctGmaPath)
                    {
                        addon.FolderPath = correctGmaPath;
                        needsUpdate = true;
                        errorHandler.HandleInfo($"Fixed GMA file path for {addonId}", "RepairAddonMetadata");
                    }
                }
                
                // Initialize WorkshopIconResolver
                if (!addon.IsGmaFile)
                {
                    string correctPath = Path.Combine(addonsPath, addonId);
                    if (Directory.Exists(correctPath) && addon.FolderPath != correctPath)
                    {
                        addon.FolderPath = correctPath;
                        needsUpdate = true;
                        errorHandler.HandleInfo($"Fixed folder path for {addonId}", "RepairAddonMetadata");
                    }
                }
                
                if (needsUpdate)
                {
                    configuration.AddonMetadata[addonId] = addon;
                    metadataUpdated = true;
                }
            }
            
            if (metadataUpdated)
            {
                await SaveConfigurationAsync();
                errorHandler.HandleInfo("Addon metadata repair completed - configuration saved", "RepairAddonMetadata");
            }
            else
            {
                errorHandler.HandleInfo("Addon metadata repair completed - no changes needed", "RepairAddonMetadata");
            }
        }
        
        /// <summary>
                // Steam URLスキームを使用してサブスクライブ
        public async Task CleanupDuplicateAddonsAsync()
        {
            errorHandler.HandleInfo("Starting duplicate addon cleanup...", "CleanupDuplicateAddons");
            
            var duplicatesFound = new List<string>();
            var cleanupOperations = new List<(string addonId, string action)>();
            
            // Initialize WorkshopIconResolver
            var addonGroups = configuration.AddonMetadata
                .GroupBy(kvp => kvp.Key)
                .Where(g => g.Count() > 1)
                .ToList();
            
            // Initialize WorkshopIconResolver
            foreach (var kvp in configuration.AddonMetadata.ToList())
            {
                var addonId = kvp.Key;
                var addon = kvp.Value;
                
                // Initialize WorkshopIconResolver
                if (!addon.IsGmaFile)
                {
                        // ディレクトリの存在確認
                    string? gmaPath = null;
                    
                    // Initialize WorkshopIconResolver
                    if (!string.IsNullOrEmpty(gmodCacheAddonsPath))
                    {
                        gmaPath = Path.Combine(gmodCacheAddonsPath, $"{addonId}.gma");
                        if (!File.Exists(gmaPath))
                        {
                            gmaPath = null;
                        }
                    }
                    
                    if (gmaPath == null && !string.IsNullOrEmpty(gmodCachePath))
                    {
                        gmaPath = Path.Combine(gmodCachePath, $"{addonId}.gma");
                        if (!File.Exists(gmaPath))
                        {
                            gmaPath = null;
                        }
                    }
                    
                    // Initialize WorkshopIconResolver
                    if (gmaPath != null)
                    {
                        duplicatesFound.Add(addonId);
                        
                        // Initialize WorkshopIconResolver
                        string directoryPath = addon.FolderPath;
                        if (!string.IsNullOrEmpty(directoryPath) && Directory.Exists(directoryPath))
                        {
                            try
                            {
                                // Initialize WorkshopIconResolver
                                if (addon.IsEnabled)
                                {
                                    string junctionPath = Path.Combine(workshopPath, addonId);
                                    if (Directory.Exists(junctionPath) && junctionService.IsJunction(junctionPath))
                                    {
                                        junctionService.RemoveJunction(junctionPath);
                                    }
                                }
                                
                    // メタデータから削除
                                Directory.Delete(directoryPath, true);
                                cleanupOperations.Add((addonId, $"Deleted duplicate directory"));
                                
                                errorHandler.HandleInfo($"Deleted duplicate directory addon {addonId}", "CleanupDuplicateAddons");
                                
                                // Initialize WorkshopIconResolver
                                addon.IsGmaFile = true;
                                addon.FolderPath = gmaPath;
                                
                                // Initialize WorkshopIconResolver
                                ReadGmaMetadata(gmaPath, addon);
                                
                                // Initialize WorkshopIconResolver
                                if (addon.IsEnabled)
                                {
                                    EnableGmaAddon(addonId);
                                }
                                
                                configuration.AddonMetadata[addonId] = addon;
                            }
                            catch (Exception ex)
                            {
                                errorHandler.HandleError(ex, $"Failed to cleanup duplicate addon {addonId}", ErrorSeverity.Error);
                            }
                        }
                    }
                }
            }
            
            // 設定を保存
            if (duplicatesFound.Count > 0)
            {
                await SaveConfigurationAsync();
                
                var summary = $"Cleanup completed. Found {duplicatesFound.Count} duplicate addons.";
                if (cleanupOperations.Count > 0)
                {
                    summary += "\nOperations performed:\n" + string.Join("\n", cleanupOperations.Select(op => $"- {op.addonId}: {op.action}"));
                }
                
                errorHandler.HandleInfo(summary, "CleanupDuplicateAddons");
            }
            else
            {
                errorHandler.HandleInfo("No duplicate addons found.", "CleanupDuplicateAddons");
            }
        }
        
        /// <summary>
        // Initialize WorkshopIconResolver
        public async Task MigrateToHardLinkSystemAsync()
        {
            errorHandler.HandleInfo("Starting migration from junction to hard link system...", "MigrateToHardLinkSystem");
            
            int migratedCount = 0;
            int failedCount = 0;
            var failedAddons = new List<string>();
            
            // Initialize WorkshopIconResolver
            foreach (var kvp in configuration.AddonMetadata.ToList())
            {
                var addonId = kvp.Key;
                var addon = kvp.Value;
                
                // Initialize WorkshopIconResolver
                if (addon.IsGmaFile)
                {
                    continue;
                }
                
                string workshopAddonPath = Path.Combine(workshopPath, addonId);
                string sourcePath = Path.Combine(addonsPath, addonId);
                string sourceGmaPath = Path.Combine(sourcePath, $"{addonId}.gma");
                
                // Initialize WorkshopIconResolver
                if (Directory.Exists(workshopAddonPath) && 
                    junctionService.IsJunction(workshopAddonPath) && 
                    File.Exists(sourceGmaPath))
                {
                    try
                    {
                        // Initialize WorkshopIconResolver
                        junctionService.RemoveJunction(workshopAddonPath);
                        
                        // Initialize WorkshopIconResolver
                        junctionService.CreateWorkshopAddonStructure(workshopPath, addonId, sourceGmaPath);
                        
                        migratedCount++;
                        errorHandler.HandleInfo($"Migrated addon {addonId} to hard link system", "MigrateToHardLinkSystem");
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        failedAddons.Add(addonId);
                        errorHandler.HandleError(ex, $"Failed to migrate addon {addonId}", ErrorSeverity.Warning);
                        
                        // Initialize WorkshopIconResolver
                        try
                        {
                            if (!Directory.Exists(workshopAddonPath))
                            {
                                CreateJunctionWithMetrics(workshopAddonPath, sourcePath);
                            }
                        }
                        catch (Exception restoreEx)
                        {
                            errorHandler.HandleError(restoreEx, $"Failed to restore junction for addon {addonId}", ErrorSeverity.Error);
                        }
                    }
                }
            }
            
            // Initialize WorkshopIconResolver
            string summary = $"Migration completed. Migrated: {migratedCount}, Failed: {failedCount}";
            if (failedAddons.Count > 0)
            {
                summary += $"\nFailed addons: {string.Join(", ", failedAddons)}";
            }
            
            errorHandler.HandleInfo(summary, "MigrateToHardLinkSystem");
            
            if (migratedCount > 0)
            {
                await SaveConfigurationAsync();
            }
        }
        
        /// <summary>
        // Initialize WorkshopIconResolver
        /// </summary>
        private async Task<HashSet<string>> CleanupDeletedWorkshopAddonsAsync(List<WorkshopAddon> currentAddons)
        {
            var deletedAddonIds = new HashSet<string>();
            try
            {
                var toRemove = new List<string>();
                var subscriptionTruthAvailable = TryGetSubscribedAddonIdSet(
                    "CleanupDeletedWorkshopAddons",
                    out var subscribedAddonIds);
                var currentAddonIds = new HashSet<string>(
                    currentAddons.Where(addon => !addon.IsLocal).Select(addon => addon.Id),
                    StringComparer.Ordinal);

                if (!PathOverrideResolver.IsDirectoryUsable(workshopPath))
                {
                    errorHandler.HandleWarning(
                        "Workshop root is unavailable; skipping deleted-addon cleanup.",
                        "CleanupDeletedWorkshopAddons");
                    return deletedAddonIds;
                }

                if (!subscriptionTruthAvailable)
                {
                    errorHandler.HandleWarning(
                        "Steam subscription state is not authoritative; skipping deleted-addon cleanup.",
                        "CleanupDeletedWorkshopAddons");
                    return deletedAddonIds;
                }
                
                // Initialize WorkshopIconResolver
                foreach (var kvp in configuration.AddonMetadata)
                {
                    var addonId = kvp.Key;
                    var addon = kvp.Value;

                    if (addon.IsLocal)
                    {
                        continue;
                    }

                    // The caller has already validated every item in the current inventory.
                    // Re-validating those directories here doubled the filesystem walk and
                    // duplicate-log volume on every startup.
                    if (currentAddonIds.Contains(addonId))
                    {
                        continue;
                    }
                    
                    bool fileExists = false;
                    
                    if (addon.IsGmaFile)
                    {
                        // GMAファイルを削除
                        string managedGmaPath = Path.Combine(addonsPath, $"{addonId}.gma");
                        string? cacheGmaPath = !string.IsNullOrEmpty(gmodCachePath) ? 
                            Path.Combine(gmodCachePath, $"{addonId}.gma") : null;
                        string? cacheManagerGmaPath = !string.IsNullOrEmpty(gmodCacheAddonsPath) ? 
                            Path.Combine(gmodCacheAddonsPath, $"{addonId}.gma") : null;

                        var cacheFileCountsAsCurrent = subscribedAddonIds.Contains(addonId);
                        
                        fileExists = File.Exists(managedGmaPath) || 
                                   (cacheFileCountsAsCurrent && cacheGmaPath != null && File.Exists(cacheGmaPath)) ||
                                   (cacheManagerGmaPath != null && File.Exists(cacheManagerGmaPath));
                    }
                    else
                    {
                        // Initialize WorkshopIconResolver
                        string managedDirPath = Path.Combine(addonsPath, addonId);
                        string workshopDirPath = Path.Combine(workshopPath, addonId);
                        
                        // Initialize WorkshopIconResolver
                        if (Directory.Exists(managedDirPath))
                        {
                            var hasFiles = Directory.GetFiles(managedDirPath, "*", SearchOption.AllDirectories).Any();
                            if (!hasFiles)
                            {
                                // Initialize WorkshopIconResolver
                                try
                                {
                                    Directory.Delete(managedDirPath, true);
                                    errorHandler.HandleInfo($"Deleted empty managed directory: {managedDirPath}", "CleanupDeletedWorkshopAddons");
                                }
                                catch (Exception ex)
                                {
                                    errorHandler.HandleError(ex, $"Failed to delete empty directory: {managedDirPath}", ErrorSeverity.Warning);
                                }
                                fileExists = false;
                            }
                            else
                            {
                                fileExists = true;
                            }
                        }
                        else
                        {
                            fileExists = Directory.Exists(workshopDirPath) &&
                                         !junctionService.IsJunction(workshopDirPath) &&
                                         DirectoryHasAddonPayload(workshopDirPath, "CleanupDeletedWorkshopAddons");
                        }
                    }
                    
                    if (!fileExists)
                    {
                        if (subscribedAddonIds.Contains(addonId))
                        {
                            addon.IsEnabled = false;
                            addon.NeedsTitleUpdate = true;
                            continue;
                        }

                        toRemove.Add(addonId);
                        errorHandler.HandleInfo($"Detected deleted workshop addon: {addonId}", "CleanupDeletedWorkshopAddons");
                    }
                }
                
                // Initialize WorkshopIconResolver
                foreach (var addonId in toRemove)
                {
                    RemoveAddonFromCurrentInventory(addonId);
                    
                // Initialize WorkshopIconResolver
                    // Initialize WorkshopIconResolver
                    try
                    {
        /// </summary>
                        string managedDirPath = Path.Combine(addonsPath, addonId);
                        if (Directory.Exists(managedDirPath))
                        {
                            Directory.Delete(managedDirPath, true);
                            errorHandler.HandleInfo($"Deleted managed directory: {managedDirPath}", "CleanupDeletedWorkshopAddons");
                        }
                        
            // 管理フォルダが存在しない場合は作成
                        string managedGmaPath = Path.Combine(addonsPath, $"{addonId}.gma");
                        if (File.Exists(managedGmaPath))
                        {
                            File.Delete(managedGmaPath);
                            errorHandler.HandleInfo($"Deleted managed GMA file: {managedGmaPath}", "CleanupDeletedWorkshopAddons");
                        }
                    }
                    catch (Exception ex)
                    {
                        errorHandler.HandleError(ex, $"Failed to delete managed files for addon {addonId}", ErrorSeverity.Warning);
                    }
                    
                    // Initialize WorkshopIconResolver
                    try
                    {
                        string thumbnailCachePath = GetThumbnailCachePath();
                        string[] thumbnailPatterns = { $"{addonId}_thumb.jpg", $"{addonId}_thumb.png", $"{addonId}.*" };
                        
                        foreach (var pattern in thumbnailPatterns)
                        {
                            var files = Directory.GetFiles(thumbnailCachePath, pattern);
                            foreach (var file in files)
                            {
                                File.Delete(file);
                                errorHandler.HandleInfo($"Deleted thumbnail cache: {file}", "CleanupDeletedWorkshopAddons");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        errorHandler.HandleError(ex, $"Failed to delete thumbnail cache for addon {addonId}", ErrorSeverity.Warning);
                    }
                }
                
                if (toRemove.Count > 0)
                {
                    errorHandler.HandleInfo($"Cleaned up {toRemove.Count} deleted workshop addons", "CleanupDeletedWorkshopAddons");
                    await SaveConfigurationAsync();
                    
                        // 管理フォルダに移動
                    foreach (var id in toRemove)
                    {
                        deletedAddonIds.Add(id);
                    }
                }
            }
            catch (Exception ex)
            {
                errorHandler.HandleError(ex, "Failed to cleanup deleted workshop addons", ErrorSeverity.Warning);
            }
            
            return deletedAddonIds;
        }
        
        /// <summary>
        // Initialize WorkshopIconResolver
        /// </summary>
        private async Task CleanupUnsubscribedAddonsAsync()
        {
            var toDelete = new List<string>();
            
            errorHandler.HandleInfo("Checking for unsubscribed addons...", "CleanupUnsubscribedAddons");

            if (!TryGetSubscribedAddonIdSet(
                    "CleanupUnsubscribedAddons",
                    out var subscribedAddonIds))
            {
                errorHandler.HandleWarning(
                    "Steam subscription state is not authoritative; skipping unsubscribed-addon cleanup.",
                    "CleanupUnsubscribedAddons");
                return;
            }
            
            foreach (var kvp in configuration.AddonMetadata)
            {
                var addonId = kvp.Key;
                var addon = kvp.Value;

                if (addon.IsLocal || subscribedAddonIds.Contains(addonId))
                {
                    continue;
                }
                var workshopPath = Path.Combine(this.workshopPath, addonId);
                
                // Initialize WorkshopIconResolver
                bool workshopExists = DirectoryHasAddonPayload(workshopPath, "CleanupUnsubscribedAddons") ||
                                    File.Exists(workshopPath) ||
                                    File.Exists(workshopPath + ".gma");
                
                if (!workshopExists)
                {
                    // Initialize WorkshopIconResolver
                    bool managedExists = false;
                    
                    if (addon.IsGmaFile)
                    {
                        // Initialize WorkshopIconResolver
                        string managedGmaPath = Path.Combine(addonsPath, $"{addonId}.gma");
                        string? cacheGmaPath = !string.IsNullOrEmpty(gmodCachePath) ? 
                            Path.Combine(gmodCachePath, $"{addonId}.gma") : null;
                        string? cacheManagerGmaPath = !string.IsNullOrEmpty(gmodCacheAddonsPath) ? 
                            Path.Combine(gmodCacheAddonsPath, $"{addonId}.gma") : null;
                        
                        managedExists = File.Exists(managedGmaPath) || 
                                      (cacheGmaPath != null && File.Exists(cacheGmaPath)) ||
                                      (cacheManagerGmaPath != null && File.Exists(cacheManagerGmaPath));
                    }
                    else
                    {
                        // Initialize WorkshopIconResolver
                        string managedDirPath = Path.Combine(addonsPath, addonId);
                        managedExists = DirectoryHasAddonPayload(managedDirPath, "CleanupUnsubscribedAddons");
                    }
                    
                    if (managedExists)
                    {
                        // Initialize WorkshopIconResolver
                        toDelete.Add(addonId);
                        errorHandler.HandleInfo($"Detected unsubscribed addon: {addonId}", "CleanupUnsubscribedAddons");
                    }
                }
            }
            
            // Check cache directory for .cache file (Garry's Mod sometimes uses .cache extension)
            foreach (var addonId in toDelete)
            {
                try
                {
            // Check managed cache directory for GMA file
                    if (configuration.AddonMetadata[addonId].IsGmaFile)
                    {
                        // Initialize WorkshopIconResolver
                        string managedGmaPath = Path.Combine(addonsPath, $"{addonId}.gma");
                        if (File.Exists(managedGmaPath))
                        {
                            File.Delete(managedGmaPath);
                            errorHandler.HandleInfo($"Deleted managed GMA file: {managedGmaPath}", "CleanupUnsubscribedAddons");
                        }
                        
                        // Initialize WorkshopIconResolver
                        if (!string.IsNullOrEmpty(gmodCacheAddonsPath))
                        {
                            string cacheGmaPath = Path.Combine(gmodCacheAddonsPath, $"{addonId}.gma");
                            if (File.Exists(cacheGmaPath))
                            {
                                File.Delete(cacheGmaPath);
                                errorHandler.HandleInfo($"Deleted cache GMA file: {cacheGmaPath}", "CleanupUnsubscribedAddons");
                            }
                        }
                    }
                    else
                    {
                        // Initialize WorkshopIconResolver
                        string managedDirPath = Path.Combine(addonsPath, addonId);
                        if (Directory.Exists(managedDirPath))
                        {
                            Directory.Delete(managedDirPath, true);
                            errorHandler.HandleInfo($"Deleted managed directory: {managedDirPath}", "CleanupUnsubscribedAddons");
                        }
                    }
                    
                    RemoveAddonFromCurrentInventory(addonId);
                    
                    // Initialize WorkshopIconResolver
                    try
                    {
                        string thumbnailCachePath = GetThumbnailCachePath();
                        string[] thumbnailPatterns = { $"{addonId}_thumb.jpg", $"{addonId}_thumb.png", $"{addonId}.*" };
                        
                        foreach (var pattern in thumbnailPatterns)
                        {
                            var files = Directory.GetFiles(thumbnailCachePath, pattern);
                            foreach (var file in files)
                            {
                                File.Delete(file);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        errorHandler.HandleError(ex, $"Failed to delete thumbnail cache for addon {addonId}", ErrorSeverity.Warning);
                    }
                }
                catch (Exception ex)
                {
                    errorHandler.HandleError(ex, $"Failed to cleanup unsubscribed addon {addonId}", ErrorSeverity.Warning);
                }
            }
            
            if (toDelete.Count > 0)
            {
                errorHandler.HandleInfo($"Cleaned up {toDelete.Count} unsubscribed addons", "CleanupUnsubscribedAddons");
                await SaveConfigurationAsync();
            }
        }
        
        /// <summary>
        // Initialize WorkshopIconResolver
        /// </summary>
        public async Task RepairCacheManagementAsync()
        {
            if (string.IsNullOrEmpty(gmodCachePath))
                return;

            errorHandler.HandleInfo("Starting cache management repair...", "RepairCacheManagement");

            // Initialize WorkshopIconResolver
            if (!Directory.Exists(gmodCacheAddonsPath))
            {
                try
                {
                    ValidatePath(gmodCacheManagerPath, "gmodCacheManagerPath");
                    ValidatePath(gmodCacheAddonsPath, "gmodCacheAddonsPath");
                    Directory.CreateDirectory(gmodCacheManagerPath);
                    Directory.CreateDirectory(gmodCacheAddonsPath);
                    errorHandler.HandleInfo("Created missing cache management folders", "RepairCacheManagement");
                }
                catch (Exception ex)
                {
                    errorHandler.HandleError(ex, "Failed to create cache management folders", ErrorSeverity.Error);
                    return;
                }
            }

            // Initialize WorkshopIconResolver
            var unmanagedGmaFiles = Directory.GetFiles(gmodCachePath, "*.gma")
                .Where(f => {
                    var id = Path.GetFileNameWithoutExtension(f);
                    return configuration.AddonMetadata.ContainsKey(id) && 
                           configuration.AddonMetadata[id].IsGmaFile;
                })
                .ToList();

            if (unmanagedGmaFiles.Any())
            {
                errorHandler.HandleInfo($"Found {unmanagedGmaFiles.Count} unmanaged GMA files, repairing...", "RepairCacheManagement");
                
                foreach (var gmaFile in unmanagedGmaFiles)
                {
                    try
                    {
                        string fileName = Path.GetFileNameWithoutExtension(gmaFile);
                        string targetPath = Path.Combine(gmodCacheAddonsPath, Path.GetFileName(gmaFile));
                        ValidatePath(gmaFile, "gmaFile");
                        ValidatePath(targetPath, "targetPath");
                        
                        // Initialize WorkshopIconResolver
                        if (!File.Exists(targetPath))
                        {
                            File.Move(gmaFile, targetPath);
                            errorHandler.HandleInfo($"Moved {fileName}.gma to managed folder", "RepairCacheManagement");
                        }
                        else
                        {
                            File.Delete(gmaFile);
                            errorHandler.HandleInfo($"Deleted duplicate {fileName}.gma from cache", "RepairCacheManagement");
                        }
                        
                        // Initialize WorkshopIconResolver
                        if (configuration.AddonMetadata[fileName].IsEnabled)
                        {
                            if (AreSameDrive(targetPath, gmaFile))
                            {
                                if (CreateHardLinkSafe(gmaFile, targetPath))
                                {
                                    errorHandler.HandleInfo($"Created hard link for {fileName}.gma", "RepairCacheManagement");
                                }
                                else
                                {
                                    CopyFileForLinkFallback(fileName, targetPath, gmaFile, "RepairCacheManagement");
                                    errorHandler.HandleInfo($"Copied {fileName}.gma back to cache", "RepairCacheManagement");
                                }
                            }
                            else
                            {
                                CopyFileForLinkFallback(fileName, targetPath, gmaFile, "RepairCacheManagement");
                                errorHandler.HandleInfo($"Copied {fileName}.gma back to cache (different drive)", "RepairCacheManagement");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (ex is StrictLinkModeException)
                        {
                            throw;
                        }

                        errorHandler.HandleError(ex, $"Failed to repair GMA file: {Path.GetFileName(gmaFile)}", ErrorSeverity.Warning);
                    }
                }
                
                await SaveConfigurationAsync();
            }
            else
            {
                errorHandler.HandleInfo("No unmanaged GMA files found", "RepairCacheManagement");
            }
            
            errorHandler.HandleInfo("Cache management repair completed", "RepairCacheManagement");
        }
        
        /// <summary>
        /// Runtime check to determine if an addon is a GMA file
        /// </summary>
        private bool IsGmaAddonRuntime(string addonId)
        {
            if (string.IsNullOrEmpty(gmodCachePath))
                return false;
                
            // Check cache directory for GMA file
            string cacheGmaPath = Path.Combine(gmodCachePath, $"{addonId}.gma");
            if (LooksLikeGmaFile(cacheGmaPath))
                return true;
                
            // Check cache directory for .cache file (Garry's Mod sometimes uses .cache extension)
            string cacheCachePath = Path.Combine(gmodCachePath, $"{addonId}.cache");
            if (File.Exists(cacheCachePath) && LooksLikeGmaFile(cacheCachePath))
                return true;
                
            // Check managed cache directory for GMA file
            if (!string.IsNullOrEmpty(gmodCacheAddonsPath))
            {
                string managedGmaPath = Path.Combine(gmodCacheAddonsPath, $"{addonId}.gma");
                if (LooksLikeGmaFile(managedGmaPath))
                    return true;
            }

            // Check workshop manager directory for GMA file
            string managedWorkshopGmaPath = Path.Combine(addonsPath, addonId, $"{addonId}.gma");
            if (LooksLikeGmaFile(managedWorkshopGmaPath))
                return true;
            
            // Legacy managed GMA location
            string legacyManagedWorkshopGmaPath = Path.Combine(addonsPath, $"{addonId}.gma");
            if (LooksLikeGmaFile(legacyManagedWorkshopGmaPath))
                return true;
            
            // Check workshop directory for GMA file structure
            string workshopAddonPath = Path.Combine(workshopPath, addonId);
            if (Directory.Exists(workshopAddonPath))
            {
                string workshopGmaPath = Path.Combine(workshopAddonPath, $"{addonId}.gma");
                if (LooksLikeGmaFile(workshopGmaPath))
                    return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Force cleanup of cache files for disabled GMA addons
        /// </summary>
        public async Task ForceCleanupDisabledGmaCacheAsync()
        {
            if (string.IsNullOrEmpty(gmodCachePath))
                return;
                
            errorHandler.HandleInfo("Starting force cleanup of disabled GMA cache files...", "ForceCleanupCache");
            int cleanedCount = 0;
            
            // Get all disabled GMA addons
            var disabledGmaAddons = configuration.AddonMetadata
                .Where(kvp => kvp.Value.IsGmaFile && !kvp.Value.IsEnabled)
                .Select(kvp => kvp.Key)
                .ToList();
                
            foreach (var addonId in disabledGmaAddons)
            {
                try
                {
                    // Check for GMA file in cache
                    string cacheGmaPath = Path.Combine(gmodCachePath, $"{addonId}.gma");
                    if (File.Exists(cacheGmaPath))
                    {
                        try
                        {
                            File.SetAttributes(cacheGmaPath, FileAttributes.Normal);
                            File.Delete(cacheGmaPath);
                            cleanedCount++;
                            errorHandler.HandleInfo($"Force deleted cache GMA: {addonId}.gma", "ForceCleanupCache");
                        }
                        catch (Exception ex)
                        {
                            errorHandler.HandleWarning($"Failed to force delete {addonId}.gma: {ex.Message}", "ForceCleanupCache");
                        }
                    }
                    
                    // Check for .cache file
                    string cacheCachePath = Path.Combine(gmodCachePath, $"{addonId}.cache");
                    if (File.Exists(cacheCachePath))
                    {
                        try
                        {
                            File.SetAttributes(cacheCachePath, FileAttributes.Normal);
                            File.Delete(cacheCachePath);
                            cleanedCount++;
                            errorHandler.HandleInfo($"Force deleted cache file: {addonId}.cache", "ForceCleanupCache");
                        }
                        catch (Exception ex)
                        {
                            errorHandler.HandleWarning($"Failed to force delete {addonId}.cache: {ex.Message}", "ForceCleanupCache");
                        }
                    }
                }
                catch (Exception ex)
                {
                    errorHandler.HandleError(ex, $"Error processing addon {addonId}", ErrorSeverity.Warning);
                }
            }
            
            errorHandler.HandleInfo($"Force cleanup completed. Cleaned {cleanedCount} cache files.", "ForceCleanupCache");
            await Task.CompletedTask;
        }
        
    }
}
