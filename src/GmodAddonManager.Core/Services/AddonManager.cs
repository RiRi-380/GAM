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
        private readonly string pendingPath;
        private readonly string addonsPath;
        private readonly string? gmodCachePath;
        private string? gmodCacheManagerPath;
        private string? gmodCacheAddonsPath;
        private string? gmodRootPath;
        private GmodAddonStateStore? gmodAddonStateStore;
        private readonly IAddonModeStrategy modeStrategy;
        
        private readonly JunctionService junctionService;
        private readonly SteamPathDetector steamPathDetector;
        private readonly SteamWorkshopService steamWorkshopService;
        private readonly UndoManager undoManager;
        private readonly IErrorHandler errorHandler;
        private readonly ExperimentEventLogger eventLogger;
        private readonly AsyncLocal<LinkOperationMetrics?> linkMetricsContext = new AsyncLocal<LinkOperationMetrics?>();
        
        private readonly System.Threading.Timer _saveDebounceTimer;
        private readonly object _saveLock = new object();
        private bool _saveRequested = false;
        private int _saveDebounceMilliseconds = 1000; // デフォルト1秒
        private int _softModeNoFileOpsNoticeLogged = 0;
        private int _sessionLogged = 0;

        public DisableMode DisableMode { get; private set; }
        public bool UnsubscribeOnHardDisable { get; set; } = false;
        public bool StrictLinkMode
        {
            get => strictLinkMode;
            set
            {
                strictLinkMode = value;
                eventLogger.StrictLinkMode = value;
            }
        }
        public bool IsExperimentContextActive => eventLogger.IsExperimentContextActive;
        public Func<bool?>? GmodRunningProvider { get; set; }
        public Func<int?>? PendingChangeCountProvider { get; set; }
        public TimeSpan StateMatchTimeout { get; set; } = TimeSpan.FromSeconds(5);
        public int StateMatchPollIntervalMs { get; set; } = 200;
        
        private Configuration configuration;
        private OperationLogManager operationLogManager;

        private bool strictLinkMode;

        private const int ERROR_NOT_SAME_DEVICE = 17;

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
            steamPathDetector = new SteamPathDetector();
            junctionService = new JunctionService();
            
            // Initialize WorkshopIconResolver
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GmodAddonManager"
            );
            var iconResolver = new WorkshopIconResolver(steamPathDetector, null, appDataPath);
            
            steamWorkshopService = new SteamWorkshopService(iconResolver);
            // Update the iconResolver with the workshop service reference
            iconResolver.SetWorkshopService(steamWorkshopService);
            
            undoManager = new UndoManager();
            errorHandler = options.ErrorHandler ?? new DefaultErrorHandler();
            eventLogger = ExperimentEventLogger.CreateDefault();
            StrictLinkMode = GetStrictLinkModeFromEnvironment();
            DisableMode = options.DisableMode;
            modeStrategy = DisableMode == DisableMode.Hard
                ? new HardAddonModeStrategy()
                : new SoftAddonModeStrategy();
            
            if (string.IsNullOrEmpty(options.CustomWorkshopPath))
            {
                workshopPath = steamPathDetector.DetectWorkshopPath();
                // Detected workshop path
            }
            else
            {
                workshopPath = options.CustomWorkshopPath;
            }

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
            
            // Detect Gmod cache path
            try
            {
                gmodCachePath = steamPathDetector.DetectGmodCachePath();
                // Detected gmodCachePath
            }
            catch (Exception ex)
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

            // Detect Gmod root path for settings management (addons.txt)
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(workshopPath, @"..\..\..\common\GarrysMod"));
                if (Directory.Exists(candidate))
                {
                    gmodRootPath = candidate;
                    gmodAddonStateStore = new GmodAddonStateStore(gmodRootPath);
                    errorHandler.HandleInfo($"Set gmodRootPath: {gmodRootPath}", "Constructor");
                }
                else
                {
                    errorHandler.HandleWarning("Garry's Mod root path not found; will not edit addons.txt state store", "Constructor");
                }
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning($"Failed to resolve GMod root path: {ex.Message}", "Constructor");
            }
            
            // デバウンスタイマーの初期化
            _saveDebounceTimer = new System.Threading.Timer(
                async _ => await ExecutePendingSaveAsync(),
                null,
                Timeout.Infinite,
                Timeout.Infinite
            );
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

                if (File.Exists(configPath))
                {
                    await LoadConfigurationAsync();
                    
                    // ジャンクションアセットが存在しない場合は追加
                    EnsureJunctionAssetExists();
                }
                else
                {
                    configuration = new Configuration();
                    configuration.CreateDefaultAssets();
                    await SaveConfigurationAsync();
                }
                
                // 起動時に未完了の操作をチェック
                await CheckIncompleteOperationsAsync();

                await MigrateExistingAddonsAsync();
                
                // 起動時のシステム整合性チェック
                await modeStrategy.ValidateSystemIntegrityAsync(this);
                
                // ジャンクション状態のアドオンを検出して更新
                await UpdateJunctionAssetAsync();
                
                // Subscribeアセットに全アドオンが含まれていることを確認
                await EnsureAllAddonsInSubscribeAssetAsync();
                
                // 初期化後、全アドオンの状態を確実に更新
                await UpdateAddonStatesAsync();
                
                // 最後にサブスクライブ解除されたアドオンをクリーンアップ
                // UpdateAddonStatesAsyncの後に実行することで、ジャンクション再作成を防ぐ
                await modeStrategy.CleanupUnsubscribedAddonsAsync(this);
                
                // Addon Manager initialization complete
            }
            catch (Exception ex)
            {
                throw; // エラーを再スローして、呼び出し元で処理できるようにする
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
            var subscribeAsset = configuration.Assets.FirstOrDefault(a => a.Id == "subscribe-system-asset");
            if (subscribeAsset == null) return;
            
            bool needsSave = false;
            
            // 全てのアドオン（フォルダとGMAの両方）をSubscribeアセットに追加
            foreach (var kvp in configuration.AddonMetadata)
            {
                // Runtime check to ensure correct IsGmaFile flag
                bool isGmaRuntime = IsGmaAddonRuntime(kvp.Key);
                if (kvp.Value.IsGmaFile != isGmaRuntime)
                {
                    errorHandler.HandleWarning(
                        $"Addon {kvp.Key} metadata mismatch: IsGmaFile={kvp.Value.IsGmaFile}, Runtime={isGmaRuntime}. Correcting metadata.",
                        "EnsureAllAddonsInSubscribeAsset"
                    );
                    kvp.Value.IsGmaFile = isGmaRuntime;
                    needsSave = true;
                }
                
                if (!subscribeAsset.Addons.Contains(kvp.Key))
                {
                    // ジャンクションアセットに属するアドオンは除外
                    var junctionAsset = configuration.Assets.FirstOrDefault(a => a.Id == "junction-system-asset");
                    if (junctionAsset != null && junctionAsset.Addons.Contains(kvp.Key))
                    {
                        continue;
                    }
                    
                    subscribeAsset.AddAddon(kvp.Key, kvp.Value.IsEnabled ? AddonState.Enabled : AddonState.Disabled);
                    needsSave = true;
                }
            }
            
            // メタデータが修正された場合は保存
            if (needsSave)
            {
                await SaveConfigurationAsync();
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
            var directories = Directory.GetDirectories(workshopPath)
                .Where(d => !Path.GetFileName(d).StartsWith("."))
                .ToList();

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
                    
                    // Skip if we're only processing specific addon IDs and this isn't one of them
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

        public Task<List<WorkshopAddon>> ScanWorkshopFolderAsync()
        {
            return modeStrategy.ScanWorkshopFolderAsync(this);
        }

        internal async Task<List<WorkshopAddon>> ScanWorkshopFolderHardAsync()
        {
            var addons = new List<WorkshopAddon>();
            var processedIds = new HashSet<string>();
            
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
                    if (configuration?.AddonMetadata != null && configuration.AddonMetadata.ContainsKey(addonId))
                    {
                        var savedAddon = configuration.AddonMetadata[addonId];
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
                            configuration.AddonMetadata[addonId] = addon;
                            addons.Add(addon);
                        }
                    }
                }
            }
            
            // 2. メタデータに保存されているが、まだ処理されていないアドオン（主にGMAファイル）を追加
            if (configuration?.AddonMetadata != null)
            {
                foreach (var kvp in configuration.AddonMetadata)
                {
                    if (!processedIds.Contains(kvp.Key))
                    {
                        // アドオンが実際に存在するか確認
                        bool addonExists = false;
                        
                        // GMAファイルの有効状態を更新
                        if (kvp.Value.IsGmaFile)
                        {
                            string gmaPath = null;
                            
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
                            // ディレクトリタイプのアドオン
                            string managedDirPath = Path.Combine(addonsPath, kvp.Key);
                            if (Directory.Exists(managedDirPath))
                            {
                                addonExists = true;
                            }
                        }
                        
                        // 存在するアドオンのみ追加
                        if (addonExists)
                        {
                            addons.Add(kvp.Value);
                            processedIds.Add(kvp.Key);
                        }
                    }
                }
            }
            
            // 3. キャッシュディレクトリのGMAファイルをスキャン
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
                    if (configuration?.AddonMetadata != null && configuration.AddonMetadata.ContainsKey(addonId))
                    {
                        var savedAddon = configuration.AddonMetadata[addonId];
                        savedAddon.FolderPath = gmaFile;
                        savedAddon.IsGmaFile = true;  // 必ずGMAファイルとしてマーク
                        savedAddon.IsEnabled = File.Exists(gmaFile);
                        
                        // メタデータを更新して保存
                        configuration.AddonMetadata[addonId] = savedAddon;
                        
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
                        if (configuration != null)
                        {
                            configuration.AddonMetadata[addonId] = addon;
                        }
                        
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
            
            return addons;
        }

        internal async Task<List<WorkshopAddon>> ScanWorkshopFolderSoftAsync()
        {
            var addons = new List<WorkshopAddon>();
            var processedIds = new HashSet<string>(StringComparer.Ordinal);

            if (Directory.Exists(workshopPath))
            {
                var workshopDirs = Directory.GetDirectories(workshopPath)
                    .Where(d => !Path.GetFileName(d).StartsWith("."))
                    .ToList();

                foreach (var directory in workshopDirs)
                {
                    var addonId = Path.GetFileName(directory);
                    if (!long.TryParse(addonId, out _))
                    {
                        continue;
                    }

                    processedIds.Add(addonId);

                    if (configuration?.AddonMetadata != null && configuration.AddonMetadata.TryGetValue(addonId, out var savedAddon))
                    {
                        savedAddon.FolderPath = directory;
                        savedAddon.IsGmaFile = IsGmaAddonRuntime(addonId);
                        savedAddon.IsEnabled = gmodAddonStateStore?.GetEnabled(addonId) ?? true;
                        addons.Add(savedAddon);
                    }
                    else
                    {
                        var addon = await ScanAddonAsync(directory);
                        if (addon != null)
                        {
                            addon.IsGmaFile = IsGmaAddonRuntime(addonId);
                            addon.IsEnabled = gmodAddonStateStore?.GetEnabled(addonId) ?? true;
                            configuration.AddonMetadata[addonId] = addon;
                            addons.Add(addon);
                        }
                    }
                }
            }

            if (configuration?.AddonMetadata != null)
            {
                foreach (var kvp in configuration.AddonMetadata)
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
                            if (File.Exists(gmaPath))
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
                            if (File.Exists(workshopGmaPath))
                            {
                                gmaPath = workshopGmaPath;
                                addonExists = true;
                            }
                        }

                        kvp.Value.IsEnabled = gmodAddonStateStore?.GetEnabled(kvp.Key) ?? true;
                        if (addonExists && gmaPath != null)
                        {
                            kvp.Value.FolderPath = gmaPath;
                        }
                    }
                    else
                    {
                        var workshopDirPath = Path.Combine(workshopPath, kvp.Key);
                        if (Directory.Exists(workshopDirPath))
                        {
                            addonExists = true;
                            kvp.Value.FolderPath = workshopDirPath;
                            kvp.Value.IsEnabled = gmodAddonStateStore?.GetEnabled(kvp.Key) ?? true;
                        }
                    }

                    if (addonExists)
                    {
                        addons.Add(kvp.Value);
                        processedIds.Add(kvp.Key);
                    }
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
                        savedAddon.IsEnabled = gmodAddonStateStore?.GetEnabled(addonId) ?? true;
                        configuration.AddonMetadata[addonId] = savedAddon;
                        addons.Add(savedAddon);
                    }
                    else
                    {
                        var addon = new WorkshopAddon(addonId, gmaFile)
                        {
                            IsGmaFile = true,
                            IsEnabled = gmodAddonStateStore?.GetEnabled(addonId) ?? true
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

            await EnsureAllAddonsInSubscribeAssetAsync();

            return addons;
        }
        
        /// <summary>
        /// Scans for truly new addons in the workshop folder that haven't been migrated yet
        /// </summary>
        public async Task<List<WorkshopAddon>> ScanForNewAddonsAsync()
        {
            var newAddons = new List<WorkshopAddon>();
            
            // First, try to get addon IDs from Steam Workshop cache (much faster)
            var cachedAddonIds = new HashSet<string>();
            try
            {
                var workshopCacheIds = SteamWorkshopCacheReader.GetSubscribedAddonIds();
                if (workshopCacheIds.Any())
                {
                    errorHandler.HandleInfo($"Found {workshopCacheIds.Count} addon IDs in Steam Workshop cache", "ScanForNewAddonsAsync");
                    cachedAddonIds = new HashSet<string>(workshopCacheIds);
                }
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning($"Failed to read Steam Workshop cache: {ex.Message}", "ScanForNewAddonsAsync");
            }
            
            // Scan actual workshop folder for directories
            var workshopDirs = Directory.GetDirectories(workshopPath)
                .Where(d => !Path.GetFileName(d).StartsWith("."))
                .ToList();
            
            foreach (var dir in workshopDirs)
            {
                string dirName = Path.GetFileName(dir);
                
                // Check if it's a valid addon ID
                if (!long.TryParse(dirName, out _))
                    continue;
                    
                // Check if we already know about this addon
                if (configuration.AddonMetadata.ContainsKey(dirName))
                    continue;
                    
                // Check if it's a junction (already managed)
                if (junctionService.IsJunction(dir))
                    continue;
                    
                // This is a new, unmanaged addon
                var addon = new WorkshopAddon
                {
                    Id = dirName,
                    Title = $"Workshop-{dirName}",
                    IsEnabled = true, // It's in the workshop folder, so it's enabled
                    IsGmaFile = IsGmaAddonRuntime(dirName) // 実際のファイルをチェック
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
                catch (Exception ex)
                {
                    // Failed to read GMA metadata - continue with basic addon info
                    errorHandler?.HandleWarning($"Failed to read GMA metadata for addon {addon.Id}", "ReadGmaMetadata");
                }
                
                newAddons.Add(addon);
            }
            
            // Check for addon IDs from Steam cache that don't have directories yet
            if (cachedAddonIds.Any())
            {
                var existingDirNames = new HashSet<string>(workshopDirs.Select(d => Path.GetFileName(d)));
                var missingAddonIds = cachedAddonIds.Except(existingDirNames)
                    .Where(id => !configuration.AddonMetadata.ContainsKey(id));
                
                foreach (var addonId in missingAddonIds)
                {
                    // This addon is subscribed but not yet downloaded/visible
                    var addon = new WorkshopAddon
                    {
                        Id = addonId,
                        Title = $"Workshop-{addonId} (Pending Download)",
                        IsEnabled = false, // Not yet available
                        IsGmaFile = false,
                        NeedsTitleUpdate = true // Mark for future update when available
                    };
                    
                    // Try to get details from Steam cache
                    try
                    {
                        var cachedDetails = SteamWorkshopCacheReader.GetAddonDetails();
                        if (cachedDetails.TryGetValue(addonId, out var info))
                        {
                            if (!string.IsNullOrEmpty(info.Title))
                                addon.Title = info.Title;
                            if (info.TimeUpdated.HasValue)
                                addon.LastUpdated = info.TimeUpdated.Value;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Failed to read from Steam cache - not critical, continue without cache data
                        errorHandler?.HandleInfo($"Failed to read Steam cache for addon {addonId}", "ReadSteamCache");
                    }
                    
                    newAddons.Add(addon);
                }
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
                        
                    // Check if we already know about this addon
                    if (configuration.AddonMetadata.ContainsKey(addonId))
                        continue;
                        
                    // This is a new GMA addon
                    var addon = new WorkshopAddon
                    {
                        Id = addonId,
                        Title = addonId,
                        Size = new FileInfo(gmaFile).Length,
                        LastUpdated = File.GetLastWriteTime(gmaFile),
                        IsGmaFile = true,
                        IsEnabled = true
                    };
                    
                    // Try to read metadata
                    ReadGmaMetadata(gmaFile, addon);
                    
                    newAddons.Add(addon);
                }
            }
            
            return newAddons;
        }

        public async Task<WorkshopAddon> ScanAddonAsync(string addonPath)
        {
            string addonId = Path.GetFileName(addonPath);
            
            if (!long.TryParse(addonId, out _))
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
        private async Task<string> ReadGmaTitleOnlyAsync(string gmaPath)
        {
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

                    // GMA Header
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
                    
                    // Read title
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
                    .Where(kvp => kvp.Value.NeedsTitleUpdate || 
                           (kvp.Value.IsGmaFile && kvp.Value.Title.StartsWith("Workshop-")))
                    .ToList();
                
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
                                    
                                    // 設定を保存
                                    await SaveConfigurationAsync();
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
            });
        }
        
	        public async Task UpdateCacheAddonTitlesAsync(IProgress<(int current, int total, string message)>? progress = null)
	        {
            var cacheAddons = configuration.AddonMetadata
                .Where(kvp => kvp.Value.IsGmaFile && 
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
                string gmaPath = null;
                
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
                    string title = await ReadGmaTitleOnlyAsync(gmaPath);
                    
                    if (!string.IsNullOrEmpty(title) && title != addonId && 
                        title.Length > 3 && !title.Equals("tag", StringComparison.OrdinalIgnoreCase))
                    {
                        return (addonId, title);
                    }
                }
                
                return (addonId, (string)null);
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

        private void ValidatePath(string path, string paramName)
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
                
                if (!isInWorkshop && !isInManager && !isInGmodCache && !isInAppDirectory)
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
	            modeStrategy.EnableAddon(this, addonId);
	        }

	        internal void EnableAddonHard(string addonId)
	        {
            // Always sync GMod logical state and ensure the workshop structure reflects the enabled view
            try
            {
                if (gmodAddonStateStore == null)
                {
                    errorHandler.HandleWarning("Garry's Mod settings path is unknown; addons.txt will not be updated.", "EnableAddon");
                }
                else
                {
                    var persisted = gmodAddonStateStore.SetEnabled(addonId, true);
                    if (!persisted)
                    {
                        errorHandler.HandleWarning($"Failed to persist addon state to addons.txt for {addonId}.", "EnableAddon");
                    }
                }
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning($"Failed to update addons.txt for {addonId}: {ex.Message}", "EnableAddon");
            }

            // Remove any legacy stub directory first (for backward compatibility)
            bool removedStub = RemoveDisabledStub(workshopPath, addonId);
            TryRestoreMovedAsideWorkshopFolder(addonId);
            
            // Check if this is a GMA file addon - both from metadata and runtime check
            var addonInfo = configuration.AddonMetadata.ContainsKey(addonId) ? configuration.AddonMetadata[addonId] : null;
            
            // Runtime check for GMA files in cache
            bool isGmaRuntime = IsGmaAddonRuntime(addonId);

            // Soft mode: only toggle addons.txt / metadata (no filesystem operations)
            // Exception: if we removed a disabled stub, fall through to restore workshop/cache presence.
            if (DisableMode == DisableMode.Soft && !removedStub)
            {
                if (Interlocked.Exchange(ref _softModeNoFileOpsNoticeLogged, 1) == 0)
                {
                    errorHandler.HandleInfo(
                        "DisableMode=Soft: ON/OFF will only update garrysmod/settings/addons.txt (no workshop/cache file operations).",
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
	                // Update metadata if mismatch detected
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
	                // GMAファイルが存在する場合、新方式を使用
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
            try
            {
                if (gmodAddonStateStore == null)
                {
                    errorHandler.HandleWarning("Garry's Mod settings path is unknown; addons.txt will not be updated.", "EnableAddon");
                }
                else
                {
                    var persisted = gmodAddonStateStore.SetEnabled(addonId, true);
                    if (!persisted)
                    {
                        errorHandler.HandleWarning($"Failed to persist addon state to addons.txt for {addonId}.", "EnableAddon");
                    }
                }
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning($"Failed to update addons.txt for {addonId}: {ex.Message}", "EnableAddon");
            }

            if (Interlocked.Exchange(ref _softModeNoFileOpsNoticeLogged, 1) == 0)
            {
                errorHandler.HandleInfo(
                    "DisableMode=Soft: ON/OFF will only update garrysmod/settings/addons.txt (no workshop/cache file operations).",
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
            modeStrategy.DisableAddon(this, addonId);
        }

        internal void DisableAddonHard(string addonId)
        {
            try
            {
                if (gmodAddonStateStore == null)
                {
                    errorHandler.HandleWarning("Garry's Mod settings path is unknown; addons.txt will not be updated.", "DisableAddon");
                }
                else
                {
                    var persisted = gmodAddonStateStore.SetEnabled(addonId, false);
                    if (!persisted)
                    {
                        errorHandler.HandleWarning($"Failed to persist addon state to addons.txt for {addonId}.", "DisableAddon");
                    }
                }
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning($"Failed to update addons.txt for {addonId}: {ex.Message}", "DisableAddon");
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
	                // ソフト無効化: ファイル構造は残し、addons.txtとメタデータのみ更新
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
	
	            if (UnsubscribeOnHardDisable)
	            {
	                TryUnsubscribeFromWorkshop(addonId);
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
            try
            {
                if (gmodAddonStateStore == null)
                {
                    errorHandler.HandleWarning("Garry's Mod settings path is unknown; addons.txt will not be updated.", "DisableAddon");
                }
                else
                {
                    var persisted = gmodAddonStateStore.SetEnabled(addonId, false);
                    if (!persisted)
                    {
                        errorHandler.HandleWarning($"Failed to persist addon state to addons.txt for {addonId}.", "DisableAddon");
                    }
                }
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning($"Failed to update addons.txt for {addonId}: {ex.Message}", "DisableAddon");
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
            try
            {
                if (gmodAddonStateStore == null)
                {
                    errorHandler.HandleWarning("Garry's Mod settings path is unknown; addons.txt will not be updated.", "EnableGmaAddon");
                }
                else
                {
                    var persisted = gmodAddonStateStore.SetEnabled(addonId, true);
                    if (!persisted)
                    {
                        errorHandler.HandleWarning($"Failed to persist addon state to addons.txt for {addonId}.", "EnableGmaAddon");
                    }
                }
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning($"Failed to update addons.txt for {addonId}: {ex.Message}", "EnableGmaAddon");
            }
            
		            // Remove any legacy stub directory first (for backward compatibility)
		            RemoveDisabledStub(workshopPath, addonId);
		            TryRestoreMovedAsideWorkshopFolder(addonId);

		            var addonInfo = configuration.AddonMetadata.ContainsKey(addonId) ? configuration.AddonMetadata[addonId] : null;

			            // Soft mode: do not touch workshop/cache files; addons.txt is enough.
			            if (DisableMode == DisableMode.Soft)
			            {
			                if (Interlocked.Exchange(ref _softModeNoFileOpsNoticeLogged, 1) == 0)
			                {
			                    errorHandler.HandleInfo(
			                        "DisableMode=Soft: ON/OFF will only update garrysmod/settings/addons.txt (no workshop/cache file operations).",
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
                                    Span<byte> buf = stackalloc byte[4];
                                    int read = stream.Read(buf);
                                    if (read == 4)
                                    {
                                        header = $"{(char)buf[0]}{(char)buf[1]}{(char)buf[2]}{(char)buf[3]}";
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
		                        try { File.SetAttributes(managedGmaPath, FileAttributes.Normal); } catch { }
		                        try { File.Delete(managedGmaPath); } catch { }
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
		                    try { File.SetAttributes(managedGmaPath, FileAttributes.Normal); } catch { }
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

	        private void TryUnsubscribeFromWorkshop(string addonId)
	        {
	            try
	            {
                var task = UnsubscribeFromWorkshopAsync(addonId);
                var completed = task.Wait(TimeSpan.FromSeconds(5));
                if (!completed)
                {
                    errorHandler.HandleWarning($"Unsubscribe request for addon {addonId} timed out; leaving subscription unchanged.", "DisableAddon");
                }
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning($"Failed to unsubscribe from workshop for addon {addonId}: {ex.Message}", "DisableAddon");
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
	
	                    try { File.SetAttributes(destinationPath, FileAttributes.Normal); } catch { }
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
            
            // Check workshop folder for junctions
            var directories = Directory.GetDirectories(workshopPath)
                .Where(d => !Path.GetFileName(d).StartsWith("."));

            foreach (var directory in directories)
            {
                if (junctionService.IsJunction(directory))
                {
                    enabledAddons.Add(Path.GetFileName(directory));
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
                        enabledAddons.Add(addonId);
                    }
                }
            }

            return enabledAddons;
        }

        public AddonStateSnapshot CaptureState()
        {
            if (DisableMode == DisableMode.Soft && gmodAddonStateStore != null)
            {
                var softStates = gmodAddonStateStore.GetAllStates();
                var softSnapshotStates = new Dictionary<string, bool>(StringComparer.Ordinal);

                foreach (var addonId in configuration.AddonMetadata.Keys
                             .Where(id => id != "*")
                             .OrderBy(id => id, StringComparer.Ordinal))
                {
                    softSnapshotStates[addonId] = softStates.TryGetValue(addonId, out var softEnabled) && softEnabled;
                }

                return BuildSnapshot(softSnapshotStates, "actual:addons.txt");
            }

            var enabledAddons = new HashSet<string>(GetEnabledAddons(), StringComparer.Ordinal);
            var states = new Dictionary<string, bool>(StringComparer.Ordinal);

            foreach (var addonId in configuration.AddonMetadata.Keys
                         .Where(id => id != "*")
                         .OrderBy(id => id, StringComparer.Ordinal))
            {
                states[addonId] = enabledAddons.Contains(addonId);
            }

            return BuildSnapshot(states, "actual");
        }

        public AddonStateSnapshot CaptureExpectedStateSnapshot()
        {
            var scope = GetExpectedScopeLabel(assetSpecific: false);
            return BuildSnapshot(BuildExpectedStates(), scope);
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
            var enabledAssets = configuration.Assets.Where(asset => asset.Enabled).ToList();
            var subscribeAsset = configuration.Assets.FirstOrDefault(a => a.Id == "subscribe-system-asset");
            return BuildExpectedStatesForAssets(enabledAssets, subscribeAsset);
        }

        private Dictionary<string, bool> BuildExpectedStatesForAssets(
            IReadOnlyList<Asset> enabledAssets,
            Asset? subscribeAsset)
        {
            var states = new Dictionary<string, bool>(StringComparer.Ordinal);

            foreach (var addonId in configuration.AddonMetadata.Keys
                         .Where(id => id != "*")
                         .OrderBy(id => id, StringComparer.Ordinal))
            {
                states[addonId] = CalculateFinalAddonState(addonId, enabledAssets, subscribeAsset);
            }

            return states;
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

            var enabledAssets = configuration.Assets.Where(a => a.Id == assetId).ToList();
            var subscribeAsset = configuration.Assets.FirstOrDefault(a => a.Id == "subscribe-system-asset");
            var states = BuildExpectedStatesForAssets(enabledAssets, subscribeAsset);
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
            var suffix = DisableMode == DisableMode.Soft ? "addons.txt" : "actual";
            return assetSpecific ? $"expected:asset:{suffix}" : $"expected:{suffix}";
        }

        private bool? GetGmodRunning()
        {
            return GmodRunningProvider?.Invoke();
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
            if (File.Exists(configPath))
            {
                try
                {
                    string json = await Task.Run(() => File.ReadAllText(configPath));
                    
                    // Validate JSON before deserialization
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        throw new InvalidOperationException("Configuration file is empty");
                    }
                    
                    try
                    {
                        // Parse JSON first to validate structure
                        var jsonObj = Newtonsoft.Json.Linq.JObject.Parse(json);
                        
                        // Deserialize with error handling
                        configuration = JsonConvert.DeserializeObject<Configuration>(json, new JsonSerializerSettings
                        {
                            Error = (sender, args) => 
                            {
                                // Log error but don't throw - allows partial deserialization
                                args.ErrorContext.Handled = true;
                            }
                        });
                    }
                    catch (Newtonsoft.Json.JsonException ex)
                    {
                        throw new InvalidOperationException($"Invalid configuration file format: {ex.Message}", ex);
                    }
                    
                    // configurationがnullの場合は新規作成
                    if (configuration == null)
                    {
                        configuration = new Configuration();
                    }
                    
                    // Migrate system asset names from Japanese to English
                    MigrateSystemAssetNames();
                    
                    // Fix any invalid CurrentVersion values
                    FixInvalidCurrentVersions();
                }
                catch (Exception ex)
                {
                    errorHandler.HandleError(ex, "Failed to load configuration", ErrorSeverity.Error);
                    configuration = new Configuration();
                }
            }
        }
        
        private void MigrateSystemAssetNames()
        {
            // Find and update Subscribe asset
            var subscribeAsset = configuration.Assets.FirstOrDefault(a => a.IsSystem && (a.Name == "サブスクライブ" || a.Name == "Subscribe"));
            if (subscribeAsset != null)
            {
                subscribeAsset.Name = "Subscribe";
                if (string.IsNullOrEmpty(subscribeAsset.Id))
                {
                    subscribeAsset.Id = "subscribe-system-asset";
                }
            }
            
            // Find and update Junction asset  
            var junctionAsset = configuration.Assets.FirstOrDefault(a => a.IsSystem && (a.Name == "ジャンクション" || a.Name == "Junction"));
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
                if (asset.CurrentVersion < 0)
                {
                    // [AddonManager] Fixing negative CurrentVersion {asset.CurrentVersion} for asset '{asset.Name}' to 0
                    asset.CurrentVersion = 0;
                }
            }
        }

        public async Task SaveConfigurationAsync()
        {
            RequestSave();
            await Task.CompletedTask; // 非同期メソッドを維持
        }
        
        /// <summary>
        /// 設定を即座に保存（デバウンスを無視）
        /// </summary>
        public async Task SaveConfigurationImmediatelyAsync()
        {
            errorHandler.HandleInfo($"SaveConfigurationImmediatelyAsync: Starting immediate save. Current assets count: {configuration.Assets.Count}", "SaveConfiguration");
            
            // デバウンスタイマーをキャンセル
            lock (_saveLock)
            {
                _saveDebounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _saveRequested = false;
            }
            
            // 即座に保存
            await SaveConfigurationInternalAsync();
            errorHandler.HandleInfo("SaveConfigurationImmediatelyAsync: Save completed", "SaveConfiguration");
        }
        
        /// <summary>
        /// 保存リクエストをキューし、デバウンスする
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
        /// 保留中の保存を実行
        /// </summary>
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
        /// 実際の保存処理
        /// </summary>
        private async Task SaveConfigurationInternalAsync()
        {
            const int maxRetries = 3;
            string json = null;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    // configurationのスナップショットを作成してシリアライズ
                    lock (_saveLock)
                    {
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
                            Version = configuration.Version,
                            LastUpdated = DateTime.UtcNow,
                            Assets = configuration.Assets.ToList(),
                            AddonMetadata = addonMetadataSnapshot,
                            JunctionHistory = junctionHistorySnapshot
                        };

                        // シリアライズ
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
                errorHandler.HandleError(new InvalidOperationException("Failed to create configuration snapshot"),
                    "Failed to save configuration - could not create snapshot", ErrorSeverity.Error);
                return;
            }

            try
            {
                // アトミックな保存処理
                var tempPath = configPath + ".tmp";
                var backupPath = configPath + ".bak";

                // 1. 一時ファイルに書き込み
                await Task.Run(() => File.WriteAllText(tempPath, json));

                // 2. 現在のファイルが存在する場合はバックアップ
                if (File.Exists(configPath))
                {
                    File.Replace(tempPath, configPath, backupPath);
                }
                else
                {
                    File.Move(tempPath, configPath);
                }

                errorHandler.HandleInfo($"Configuration saved successfully (atomic). Assets count: {configuration.Assets.Count}", "SaveConfiguration");
            }
            catch (Exception ex)
            {
                errorHandler.HandleError(ex, "Failed to save configuration", ErrorSeverity.Error);

                // 一時ファイルの削除を試みる
                try
                {
                    var tempPath = configPath + ".tmp";
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (Exception cleanupEx)
                {
                    // Failed to clean up temp file - not critical
                    errorHandler.HandleInfo($"Failed to clean up temp file", "SaveConfiguration");
                }
            }
        }
        
        /// <summary>
        /// デバウンス時間を設定
        /// </summary>
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

        public Dictionary<string, WorkshopAddon> GetAllAddons()
        {
            return configuration.AddonMetadata;
        }

        public void CreateAsset(string name)
        {
            errorHandler.HandleInfo($"CreateAsset: Creating asset with name: {name}", "CreateAsset");
            var asset = new Asset(name);
            configuration.Assets.Add(asset);
            errorHandler.HandleInfo($"CreateAsset: Asset created with ID: {asset.Id}, Total assets: {configuration.Assets.Count}", "CreateAsset");
            
            // Undo記録
            undoManager.RecordAction(new UndoAction(UndoActionType.AssetCreated, $"アセット「{name}」を作成")
            {
                AssetId = asset.Id,
                AssetName = name
            });
        }

        public void DeleteAsset(string assetId)
        {
            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId && !a.IsSystem);
            if (asset != null)
            {
                // Undo記録
                undoManager.RecordAction(new UndoAction(UndoActionType.AssetDeleted, $"アセット「{asset.Name}」を削除")
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

        public void AddAddonToAsset(string assetId, string addonId, AddonState state = AddonState.Enabled)
        {
            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId);
            if (asset != null && !asset.ContainsAllAddons())
            {
                var operationId = eventLogger.NewOperationId();
                var beforeSnapshot = CaptureState();
                var beforeHash = ComputeStateHash(beforeSnapshot);
                var stopwatch = Stopwatch.StartNew();

                // Undo記録
                var addonInfo = configuration.AddonMetadata.ContainsKey(addonId) 
                    ? configuration.AddonMetadata[addonId] 
                    : null;
                var addonName = addonInfo?.Title ?? addonId;
                
                undoManager.RecordAction(new UndoAction(
                    UndoActionType.AddonAddedToAsset,
                    $"「{addonName}」を「{asset.Name}」に追加")
                {
                    AssetId = assetId,
                    AssetName = asset.Name,
                    AddonId = addonId,
                    AddonName = addonName,
                    AddonState = state
                });
                
                // ジャンクションアセットに追加する場合
                if (assetId == "junction-system-asset")
                {
                    // 元のアセットを記録
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
                    
                    // 他のアセットから削除
                    foreach (var otherAsset in configuration.Assets)
                    {
                        if (otherAsset.Id != "junction-system-asset")
                        {
                            if (otherAsset.ContainsAllAddons())
                            {
                                // 全アドオンを含むアセットの場合は、除外状態に設定
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

        // バッチ処理用メソッド - 大量のアドオンを効率的に追加
        public void AddAddonsToAssetBatch(string assetId, List<string> addonIds, AddonState state = AddonState.Enabled)
        {
            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId);
            if (asset != null && !asset.ContainsAllAddons() && addonIds.Count > 0)
            {
                // Undo記録（バッチ全体で1つ）
                undoManager.RecordAction(new UndoAction(
                    UndoActionType.AddonAddedToAsset,
                    $"{addonIds.Count}個のアドオンを「{asset.Name}」に追加")
                {
                    AssetId = assetId,
                    AssetName = asset.Name,
                    AddonId = string.Join(",", addonIds), // 複数のIDをカンマ区切りで保存
                    AddonState = state
                });
                
                // 全てのアドオンを追加（状態更新なし）
                foreach (var addonId in addonIds)
                {
                    asset.AddAddon(addonId, state);
                }
                
                // 最後に一度だけ状態を更新
                UpdateAddonStates();
            }
        }

        public void RemoveAddonFromAsset(string assetId, string addonId)
        {
            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId);
            if (asset != null && !asset.ContainsAllAddons())
            {
                var operationId = eventLogger.NewOperationId();
                var beforeSnapshot = CaptureState();
                var beforeHash = ComputeStateHash(beforeSnapshot);
                var stopwatch = Stopwatch.StartNew();

                // 現在の状態を記録
                var currentState = asset.GetAddonState(addonId);
                
                // Undo記録
                var addonInfo = configuration.AddonMetadata.ContainsKey(addonId) 
                    ? configuration.AddonMetadata[addonId] 
                    : null;
                var addonName = addonInfo?.Title ?? addonId;
                
                undoManager.RecordAction(new UndoAction(
                    UndoActionType.AddonRemovedFromAsset,
                    $"「{addonName}」を「{asset.Name}」から削除")
                {
                    AssetId = assetId,
                    AssetName = asset.Name,
                    AddonId = addonId,
                    AddonName = addonName,
                    AddonState = currentState
                });
                
                try
                {
                    asset.RemoveAddon(addonId);
                    UpdateAddonStates();

                    stopwatch.Stop();
                    var afterSnapshot = CaptureState();
                    var afterHash = ComputeStateHash(afterSnapshot);

                    LogExperimentEvent(
                        "AssetRemoveAddon",
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
                        : "asset_remove_failed";

                    LogExperimentEvent(
                        "AssetRemoveAddon",
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

        public async Task EnableAssetAsync(string assetId)
        {
            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId);
            if (asset == null) return;
            
            // Undo記録
            undoManager.RecordAction(new UndoAction(UndoActionType.AssetEnabled, $"アセット「{asset.Name}」を有効化")
            {
                AssetId = assetId,
                AssetName = asset.Name,
                PreviousEnabledState = false
            });

            asset.Enabled = true;
            
            // アセット内のすべてのアドオンを有効化
            var addonIds = asset.ContainsAllAddons() 
                ? configuration.AddonMetadata.Keys.ToList()
                : asset.Addons.ToList();
                
            foreach (var addonId in addonIds)
            {
                // 除外されているアドオンはスキップ
                if (asset.GetAddonState(addonId) != AddonState.Excluded)
                {
                    asset.SetAddonState(addonId, AddonState.Enabled);
                }
            }
            
            // アドオン状態を更新
            await UpdateAddonStatesAsync();
        }

        public async Task DisableAssetAsync(string assetId)
        {
            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId);
            if (asset == null) return;
            
            // Undo記録
            undoManager.RecordAction(new UndoAction(UndoActionType.AssetDisabled, $"アセット「{asset.Name}」を無効化")
            {
                AssetId = assetId,
                AssetName = asset.Name,
                PreviousEnabledState = true
            });

            asset.Enabled = false;
            
            // アセット内のすべてのアドオンを無効化
            var addonIds = asset.ContainsAllAddons() 
                ? configuration.AddonMetadata.Keys.ToList()
                : asset.Addons.ToList();
                
            foreach (var addonId in addonIds)
            {
                // 現在の状態が除外でない場合のみ無効化
                if (asset.GetAddonState(addonId) != AddonState.Excluded)
                {
                    asset.SetAddonState(addonId, AddonState.Disabled);
                }
            }
            
            // アドオン状態を更新
            await UpdateAddonStatesAsync();
        }

        public async Task<AssetApplyResult> ApplyAssetExclusiveAsync(string assetId)
        {
            var result = new AssetApplyResult { AssetId = assetId };
            var operationId = eventLogger.NewOperationId();
            var beforeSnapshot = CaptureState();
            var beforeHash = ComputeStateHash(beforeSnapshot);
            var beforeHashScope = beforeSnapshot.Source;
            result.BeforeHash = beforeHash;

            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId);
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

                matchResult = await UpdateAddonStatesInternalAsync(logEvents: true, parentOperationId: operationId);
                await SaveConfigurationAsync();

                result.Success = matchResult.Matched;
                afterSnapshot = matchResult.Snapshot;
                expectedSnapshot = matchResult.ExpectedSnapshot;
                result.AfterHash = ComputeStateHash(afterSnapshot);
                result.ExpectedHash = ComputeStateHash(expectedSnapshot);
                errorCode = matchResult.Matched ? null : "state_mismatch";
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
            var field = steamWorkshopService.GetType().GetField("_iconResolver", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return field?.GetValue(steamWorkshopService) as IIconResolver;
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
        /// 全アドオンの有効/無効状態を更新
        /// </summary>
        public async Task UpdateAddonStatesAsync()
        {
            await UpdateAddonStatesInternalAsync(logEvents: true, parentOperationId: null);
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

        private async Task<StateMatchResult> UpdateAddonStatesInternalAsync(bool logEvents, string? parentOperationId = null)
        {
            var allAddonIds = configuration.AddonMetadata.Keys
                .Where(addonId => addonId != "*")
                .ToList();

            var expectedStates = BuildExpectedStates();
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

            try
            {
                var tasks = allAddonIds.Select(addonId => Task.Run(() =>
                {
                    var finalState = CalculateFinalAddonState(addonId);
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
                })).ToList();

                try
                {
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

                var metrics = linkMetrics?.ToEventMetrics();
                var matchResult = await WaitForExpectedStateAsync(expectedStates, metrics);
                stopwatch.Stop();

                if (eventLogger.IsExperimentContextActive)
                {
                    await SaveConfigurationImmediatelyAsync();
                }

                if (logEvents)
                {
                    var afterHash = ComputeStateHash(matchResult.Snapshot);
                    var expectedHash = ComputeStateHash(matchResult.ExpectedSnapshot);
                    var result = updateError == null && matchResult.Matched ? "success" : "fail";
                    var errorCode = updateError != null
                        ? "update_failed"
                        : (matchResult.Matched ? null : "state_mismatch");

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

            var currentSnapshot = CaptureState();
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
                currentSnapshot = CaptureState();
            }

            var finalMatch = string.Equals(currentSnapshot.NormalizedState, expectedNormalized, StringComparison.Ordinal);
            return new StateMatchResult(currentSnapshot, expectedSnapshot, finalMatch, stopwatch.ElapsedMilliseconds, metrics);
        }
        
        /// <summary>
        /// 全アドオンの有効/無効状態を更新（同期版 - 内部使用）
        /// </summary>
        private void UpdateAddonStates()
        {
            var allAddonIds = configuration.AddonMetadata.Keys.ToList();
            
            foreach (var addonId in allAddonIds)
            {
                // "*" は特殊な値なのでスキップ
                if (addonId == "*")
                {
                    continue;
                }
                
                var finalState = CalculateFinalAddonState(addonId);
                
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
        }
        
        /// <summary>
        /// アドオンの最終的な有効/無効状態を計算
        /// </summary>
        private bool CalculateFinalAddonState(string addonId)
        {
            var subscribeAsset = configuration.Assets.FirstOrDefault(a => a.Id == "subscribe-system-asset");
            var enabledAssets = configuration.Assets.Where(a => a.Enabled).ToList();
            return CalculateFinalAddonState(addonId, enabledAssets, subscribeAsset);
        }

        private static bool CalculateFinalAddonState(
            string addonId,
            IReadOnlyList<Asset> enabledAssets,
            Asset? subscribeAsset)
        {
            // 除外されているかチェック
            foreach (var asset in enabledAssets)
            {
                if (asset.ContainsAllAddons() || asset.Addons.Contains(addonId))
                {
                    var state = asset.GetAddonState(addonId);
                    if (state == AddonState.Excluded)
                    {
                        return false; // 除外されている場合は必ず無効
                    }
                }
            }

            // サブスクライブアセットの状態をチェック
            bool isInSubscribe = false;
            bool isSubscribeEnabled = false;
            AddonState subscribeState = AddonState.Disabled;

            if (subscribeAsset != null)
            {
                isSubscribeEnabled = enabledAssets.Any(asset => asset.Id == subscribeAsset.Id);
                if (subscribeAsset.ContainsAllAddons() || subscribeAsset.Addons.Contains(addonId))
                {
                    isInSubscribe = true;
                    subscribeState = subscribeAsset.GetAddonState(addonId);
                }
            }

            // 他のアセットで有効になっているかチェック
            foreach (var asset in enabledAssets)
            {
                if (asset.IsSystem) continue;

                if (asset.ContainsAllAddons() || asset.Addons.Contains(addonId))
                {
                    var state = asset.GetAddonState(addonId);

                    if (state == AddonState.Enabled)
                    {
                        return true; // 有効状態のアセットがあれば有効
                    }
                    else if (state == AddonState.Disabled)
                    {
                        // 無効の場合、サブスクライブアセットが有効な場合のみサブスクライブに依存
                        if (isInSubscribe && isSubscribeEnabled && subscribeState != AddonState.Excluded)
                        {
                            return true;
                        }
                    }
                }
            }

            // どのアセットにも含まれていない、または全て無効の場合
            // サブスクライブアセットの状態に従う
            if (subscribeAsset != null)
            {
                // サブスクライブアセットが有効で、かつアドオンが有効状態の場合のみtrue
                return isSubscribeEnabled && isInSubscribe && subscribeState == AddonState.Enabled;
            }

            return false;
        }
        
        /// <summary>
        /// アドオンの状態を設定
        /// </summary>
        public void SetAddonState(string assetId, string addonId, AddonState state)
        {
            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId);
            if (asset != null && (asset.Addons.Contains(addonId) || asset.ContainsAllAddons()))
            {
                var operationId = eventLogger.NewOperationId();
                var beforeSnapshot = CaptureState();
                var beforeHash = ComputeStateHash(beforeSnapshot);
                var stopwatch = Stopwatch.StartNew();

                // 現在の状態を記録
                var previousState = asset.GetAddonState(addonId);
                
                // Undo記録
                var addonInfo = configuration.AddonMetadata.ContainsKey(addonId) 
                    ? configuration.AddonMetadata[addonId] 
                    : null;
                var addonName = addonInfo?.Title ?? addonId;
                
                undoManager.RecordAction(new UndoAction(
                    UndoActionType.AddonStateChanged, 
                    $"「{addonName}」の状態を{GetStateDisplayName(state)}に変更")
                {
                    AssetId = assetId,
                    AssetName = asset.Name,
                    AddonId = addonId,
                    AddonName = addonName,
                    PreviousAddonState = previousState,
                    NewAddonState = state
                });
                
                asset.SetAddonState(addonId, state);
                try
                {
                    // 単一のアドオンの状態だけを更新（軽量化）
                    UpdateSingleAddonState(addonId);

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
        
        private string GetStateDisplayName(AddonState state)
        {
            switch (state)
            {
                case AddonState.Enabled: return "有効";
                case AddonState.Disabled: return "無効";
                case AddonState.Excluded: return "除外";
                default: return state.ToString();
            }
        }
        
        /// <summary>
        /// 単一のアドオンの状態を更新
        /// </summary>
        private void UpdateSingleAddonState(string addonId)
        {
            try
            {
                // "*" は特殊な値なのでスキップ（デバッグレベルでログ）
                if (addonId == "*")
                {
                    // ワイルドカードは正常な動作なので、警告ではなくデバッグレベルでログ
                    errorHandler.HandleInfo("Skipping wildcard addon ID '*' (this is normal behavior)", "UpdateSingleAddonState");
                    return;
                }
                
                // アドオンIDが実際に存在するか確認
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
            }
        }
        
        /// <summary>
        /// Undoマネージャーを取得
        /// </summary>
        public UndoManager GetUndoManager() => undoManager;
        
        /// <summary>
        /// 最後の操作を元に戻す
        /// </summary>
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

            try
            {
                switch (action.Type)
                {
                    case UndoActionType.AssetCreated:
                        // 作成されたアセットを削除
                        if (action.AssetId != null)
                        {
                            configuration.Assets.RemoveAll(a => a.Id == action.AssetId);
                            await SaveConfigurationAsync();
                        }
                        break;
                        
                    case UndoActionType.AssetDeleted:
                        // 削除されたアセットを復元
                        if (action.DeletedAsset != null)
                        {
                            configuration.Assets.Add(action.DeletedAsset);
                            await SaveConfigurationAsync();
                            UpdateAddonStates();
                        }
                        break;
                        
                    case UndoActionType.AssetEnabled:
                    case UndoActionType.AssetDisabled:
                        // アセットの有効/無効を元に戻す
                        if (action.AssetId != null)
                        {
                            var asset = configuration.Assets.FirstOrDefault(a => a.Id == action.AssetId);
                            if (asset != null && action.PreviousEnabledState.HasValue)
                            {
                                asset.Enabled = action.PreviousEnabledState.Value;
                                await SaveConfigurationAsync();
                                UpdateAddonStates();
                            }
                        }
                        break;
                        
                    case UndoActionType.AddonStateChanged:
                        // アドオンの状態を元に戻す
                        if (action.AssetId != null && action.AddonId != null && action.PreviousAddonState.HasValue)
                        {
                            var asset = configuration.Assets.FirstOrDefault(a => a.Id == action.AssetId);
                            if (asset != null)
                            {
                                asset.SetAddonState(action.AddonId, action.PreviousAddonState.Value);
                                await SaveConfigurationAsync();
                                UpdateAddonStates();
                            }
                        }
                        break;
                        
                    case UndoActionType.AddonAddedToAsset:
                        // アドオンをアセットから削除
                        if (action.AssetId != null && action.AddonId != null)
                        {
                            var asset = configuration.Assets.FirstOrDefault(a => a.Id == action.AssetId);
                            if (asset != null)
                            {
                                asset.RemoveAddon(action.AddonId);
                                await SaveConfigurationAsync();
                                UpdateAddonStates();
                            }
                        }
                        break;
                        
                    case UndoActionType.AddonRemovedFromAsset:
                        // アドオンをアセットに追加
                        if (action.AssetId != null && action.AddonId != null)
                        {
                            var asset = configuration.Assets.FirstOrDefault(a => a.Id == action.AssetId);
                            if (asset != null && action.AddonState.HasValue)
                            {
                                asset.AddAddon(action.AddonId, action.AddonState.Value);
                                await SaveConfigurationAsync();
                                UpdateAddonStates();
                            }
                        }
                        break;
                }
                
                success = true;
                return true;
            }
            catch (Exception ex)
            {
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
        
        /// <summary>
        /// ジャンクションアセットが存在することを確認
        /// </summary>
        private void EnsureJunctionAssetExists()
        {
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
        /// ジャンクション状態のアドオンを検出してジャンクションアセットを更新
        /// </summary>
        public async Task UpdateJunctionAssetAsync()
        {
            try
            {
                var junctionAsset = configuration.Assets.FirstOrDefault(a => a.Id == "junction-system-asset");
                if (junctionAsset == null) return;
                
                // 全アドオンをチェック
                var allAddons = configuration.AddonMetadata;
                var orphanedAddons = new List<string>();
                
                // 他のアセットに属しているアドオンを収集
                var addonsInOtherAssets = new HashSet<string>();
                foreach (var asset in configuration.Assets)
                {
                    if (asset.Id != "junction-system-asset")
                    {
                        if (asset.ContainsAllAddons())
                        {
                            // 全アドオンを含むアセットがある場合は、全てのアドオンが管理されている
                            addonsInOtherAssets.UnionWith(allAddons.Keys);
                        }
                        else
                        {
                            addonsInOtherAssets.UnionWith(asset.Addons);
                        }
                    }
                }
                
                // どのアセットにも属していないアドオンを探す
                foreach (var addon in allAddons)
                {
                    if (!addonsInOtherAssets.Contains(addon.Key))
                    {
                        var addonPath = Path.Combine(workshopPath, addon.Key);
                        var sourcePath = Path.Combine(addonsPath, addon.Key);
                        
                        // アドオンが実際に存在するかチェック
                        bool addonExists = false;
                        
                        // Check if it's a GMA file addon
                        if (addon.Value.IsGmaFile)
                        {
                            string gmaCachePath = Path.Combine(gmodCachePath ?? "", addon.Key + ".gma");
                            string gmaSourcePath = Path.Combine(addonsPath, addon.Key + ".gma");
                            
                            // GMAファイルが管理フォルダまたはキャッシュに存在するか確認
                            addonExists = File.Exists(gmaSourcePath) || File.Exists(gmaCachePath);
                            
                            // GMA file exists in managed folder = disabled
                            if (addonExists && File.Exists(gmaSourcePath) && !File.Exists(gmaCachePath))
                            {
                                orphanedAddons.Add(addon.Key);
                            }
                        }
                        else
                        {
                            // ソースディレクトリが存在するかチェック
                            addonExists = Directory.Exists(sourcePath) || 
                                        (Directory.Exists(addonPath) && !junctionService.IsJunction(addonPath));
                            
                            if (addonExists && Directory.Exists(sourcePath))
                            {
                                // ジャンクションの存在をチェック
                                bool hasJunction = Directory.Exists(addonPath) && junctionService.IsJunction(addonPath);
                                
                                // どのアセットにも属しておらず、ジャンクションが存在しない = 孤立した無効化アドオン
                                if (!hasJunction)
                                {
                                    orphanedAddons.Add(addon.Key);
                                }
                            }
                        }
                        
                        // アドオンが存在しない場合はスキップ（Workshopから削除された）
                        if (!addonExists)
                        {
                            continue;
                        }
                    }
                }
                
                // ジャンクションアセットを更新（明示的に追加されたものは保持）
                // 孤立したアドオンのみを追加
                foreach (var addonId in orphanedAddons)
                {
                    // "*" は特殊な値なので追加しない
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
                
                // ジャンクションアセットから削除すべきアドオンを特定
                // ただし、手動で追加されたアドオンは保持する
                var toRemove = new List<string>();
                foreach (var addonId in junctionAsset.Addons)
                {
                    // "*" は特殊な値なのでスキップ
                    if (addonId == "*")
                    {
                        errorHandler.HandleWarning("Removing wildcard '*' from junction asset", "UpdateJunctionAsset");
                        toRemove.Add(addonId);
                        continue;
                    }
                    
                    // 他のアセットに存在し、かつ孤立していないアドオンのみ削除対象
                    // ただし、AddonStatesに記録されている（手動追加された）アドオンは削除しない
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
        /// ジャンクションアドオンを元のアセットに戻す
        /// </summary>
        public void RestoreAddonFromJunction(string addonId)
        {
            // ジャンクションアセットから削除
            var junctionAsset = configuration.Assets.FirstOrDefault(a => a.Id == "junction-system-asset");
            if (junctionAsset != null)
            {
                junctionAsset.RemoveAddon(addonId);
            }
            
            // 元のアセットに戻す
            if (configuration.JunctionHistory.TryGetValue(addonId, out var sourceAssetIds))
            {
                foreach (var assetId in sourceAssetIds)
                {
                    var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId);
                    if (asset != null)
                    {
                        if (asset.ContainsAllAddons())
                        {
                            // 全アドオンを含むアセットの場合は、除外状態を解除
                            asset.AddonStates.Remove(addonId);
                        }
                        else
                        {
                            asset.AddAddon(addonId, AddonState.Enabled);
                        }
                    }
                }
                
                // 履歴から削除
                configuration.JunctionHistory.Remove(addonId);
            }
            
            UpdateAddonStates();
        }
        
        /// <summary>
        /// アドオンの元のアセットを取得
        /// </summary>
        public List<string> GetAddonSourceAssets(string addonId)
        {
            if (configuration.JunctionHistory.TryGetValue(addonId, out var sourceAssetIds))
            {
                return new List<string>(sourceAssetIds);
            }
            return new List<string>();
        }
        
        /// <summary>
        /// 移行中に既存ディレクトリが見つかった場合の処理
        /// </summary>
        private void HandleExistingDirectoryDuringMigration(string directory, string targetPath, string dirName)
        {
            // 実体フォルダが存在する場合、管理フォルダに移動してからジャンクション作成
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
                        // ロールバックは難しいので、テンポラリを残して警告に留める
                        errorHandler.HandleError(ex,
                            $"Failed to merge addon {dirName} contents into managed folder. Leaving temp folder: {tempPath}",
                            ErrorSeverity.Warning);
                    }
                }
            }
            catch
            {
                // 失敗した場合は元に戻す
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
        /// ディレクトリをマージする
        /// </summary>
        private void MergeDirectories(string source, string destination)
        {
            ValidatePath(source, "source");
            ValidatePath(destination, "destination");
            
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                // .NET Standard 2.0対応のため手動で相対パスを計算
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
            errorHandler.HandleInfo("Starting full reset of addon manager", "ResetManager");
            
            // RestoreOriginalStateAsyncを使用して、すべてのアドオンを元の状態に戻す
            await RestoreOriginalStateAsync();
            
            // 設定を再初期化
            configuration = new Configuration();
            
            // ディレクトリを再作成して初期化
            await InitializeAsync();
            
            errorHandler.HandleInfo("Full reset completed successfully", "ResetManager");
        }
        
        public async Task RestoreOriginalStateAsync()
        {
            errorHandler.HandleInfo("Starting RestoreOriginalStateAsync", "RestoreOriginalState");
            
            // ステップ1: すべてのジャンクション/ハードリンクを削除
            await RemoveAllJunctionsAndHardLinksAsync();
            
            // ステップ2: 管理ディレクトリからファイルを元の場所に戻す
            await RestoreManagedAddonsAsync();
            
            // ステップ3: 管理ディレクトリとキャッシュディレクトリを削除
            await CleanupManagerDirectoriesAsync();
            
            // ステップ4: 設定ファイルを削除（完全にクリーンな状態に）
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
            errorHandler.HandleInfo("Removing all junctions and hard links", "RemoveAllJunctionsAndHardLinks");
            
            // Workshopパス内のすべてのエントリをチェック
            foreach (var entry in Directory.GetDirectories(workshopPath))
            {
                var dirName = Path.GetFileName(entry);
                
                try
                {
                    // ジャンクションの場合は削除
                    if (junctionService.IsJunction(entry))
                    {
                        errorHandler.HandleInfo($"Removing junction: {dirName}", "RemoveAllJunctionsAndHardLinks");
                        junctionService.RemoveJunction(entry);
                    }
                    // 通常のディレクトリでGMAハードリンクを含む可能性がある場合
                    else
                    {
                        var gmaPath = Path.Combine(entry, $"{dirName}.gma");
                        if (File.Exists(gmaPath))
                        {
                            // ハードリンクかどうかチェックして削除
                            var managedGmaPath = Path.Combine(addonsPath, dirName, $"{dirName}.gma");
                            if (File.Exists(managedGmaPath) && junctionService.IsHardLink(gmaPath, managedGmaPath))
                            {
                                errorHandler.HandleInfo($"Removing hard link: {gmaPath}", "RemoveAllJunctionsAndHardLinks");
                                junctionService.RemoveHardLink(gmaPath);
                                // 空のディレクトリも削除
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
            
            // キャッシュディレクトリ内のハードリンクも削除
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
            errorHandler.HandleInfo("Restoring managed addons to original locations", "RestoreManagedAddons");
            
            // 管理ディレクトリ内のすべてのアドオンを元の場所に戻す
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
                        // 元の場所にディレクトリが存在しない場合のみ移動
                        if (!Directory.Exists(originalPath))
                        {
                            errorHandler.HandleInfo($"Moving addon {addonId} back to workshop", "RestoreManagedAddons");
                            Directory.Move(managedAddonPath, originalPath);
                        }
                        else
                        {
                            // 既に存在する場合はマージ処理
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
            
            // キャッシュ管理ディレクトリ内のGMAファイルも元の場所に戻す
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
        
        private async Task CleanupManagerDirectoriesAsync()
        {
            errorHandler.HandleInfo("Cleaning up manager directories", "CleanupManagerDirectories");
            
            // 管理ディレクトリを削除
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
            
            // キャッシュ管理ディレクトリを削除
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
                    // エラーは無視して続行
                }
            }
        }
        
        /// <summary>
        /// リソースのクリーンアップ
        /// </summary>
        /// <summary>
        /// 未完了の操作をチェックして復旧オプションを提供
        /// </summary>
        private async Task CheckIncompleteOperationsAsync()
        {
            var incompleteLogs = operationLogManager.GetIncompleteLogs();
            if (incompleteLogs.Count > 0)
            {
                errorHandler.HandleWarning($"Found {incompleteLogs.Count} incomplete operations from previous session", "CheckIncompleteOperations");
                
                // ここでUIに通知するか、自動復旧を試みる
                // 現時点では警告のみ
                foreach (var log in incompleteLogs)
                {
                    errorHandler.HandleWarning(
                        $"Incomplete {log.Type} operation from {log.StartTime:yyyy-MM-dd HH:mm:ss} with {log.Items.Count} items",
                        "IncompleteOperation"
                    );
                    
                    // 操作をクリーンアップ（必要に応じて復旧処理を追加）
                    operationLogManager.RemoveLog(log.Id);
                }
            }
        }
        
        /// <summary>
        /// システムの整合性をチェックして修復
        /// </summary>
	        private async Task ValidateSystemIntegrityAsync()
	        {
	            errorHandler.HandleInfo("Starting system integrity check...", "ValidateSystemIntegrity");
            
            var repairCount = 0;
            
            // 1. 設定ファイルのバックアップチェック
            var backupPath = configPath + ".bak";
            if (!File.Exists(backupPath) && File.Exists(configPath))
            {
                try
                {
                    File.Copy(configPath, backupPath, true);
                    errorHandler.HandleInfo("Created configuration backup", "ValidateSystemIntegrity");
                }
                catch (Exception ex)
                {
                    // Failed to create backup - log but continue validation
                    errorHandler.HandleWarning($"Failed to create configuration backup", "ValidateAndRepairConfiguration");
                }
            }
            
	            // 2. ジャンクション/ハードリンクの検証
	            foreach (var kvp in configuration.AddonMetadata.ToList())
	            {
	                var addon = kvp.Value;
	                var addonId = kvp.Key;
	                
	                try
	                {
	                    if (addon.IsGmaFile)
	                    {
	                        // GMAファイルのハードリンクチェック
	                        if (!string.IsNullOrEmpty(gmodCachePath))
	                        {
	                            var cachePath = Path.Combine(gmodCachePath, addonId + ".gma");
	                            
	                            // 有効化されるべきアドオンのみ、キャッシュ側のリンク欠損を修復する
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
	                        // フォルダアドオンのジャンクションチェック（Workshop側にリンクがあるのが正）
	                        var workshopAddonPath = Path.Combine(workshopPath, addonId);
	                        var managedAddonPath = Path.Combine(addonsPath, addonId);

	                        // 期待される最終状態に基づいて修復（起動直後にUpdateAddonStatesAsyncも走るため、ここは安全側に寄せる）
	                        var shouldBeEnabled = CalculateFinalAddonState(addonId);

	                        if (shouldBeEnabled)
	                        {
	                            // 管理フォルダ実体がある場合のみ、Workshop側の欠損を補う
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
	                                    // 既にジャンクションなら、CreateJunctionの同一ターゲット判定に任せて整合性を取る
	                                    CreateJunctionWithMetrics(workshopAddonPath, managedAddonPath);
	                                }
	                                else
	                                {
	                                    // まずGAMのスタブ(.gam_disabled)なら安全に差し替え可能
	                                    if (RemoveDisabledStub(workshopPath, addonId))
	                                    {
	                                        errorHandler.HandleWarning($"Repairing workshop stub for {addonId}", "ValidateSystemIntegrity");
	                                        CreateJunctionWithMetrics(workshopAddonPath, managedAddonPath);
	                                        repairCount++;
	                                    }
	                                    else
	                                    {
	                                        // 空ディレクトリ（移行失敗などで残ることがある）なら安全に差し替える
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
	                                            // 実体フォルダ（Steamが生成/再DL等）の場合は自動で移動・削除せず警告のみ
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
	                            // 無効なのにWorkshop側にジャンクションが残っている場合は削除（実体フォルダは触らない）
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

            // 未保存データを即座に保存
            _saveDebounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            if (_saveRequested)
            {
                SaveConfigurationInternalAsync().GetAwaiter().GetResult();
            }
            _saveDebounceTimer?.Dispose();
        }
        
        #region Hard Link Utilities
        
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
        
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFileAttributes(string lpFileName);
        
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);
        
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(
            IntPtr hFile,
            out BY_HANDLE_FILE_INFORMATION lpFileInformation);
        
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
                
                // Create hard link
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
                    // ファイルハンドルを取得
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

                    // 同じボリューム、同じFileIndexなら同一ファイル（ハードリンク）
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
        /// 既存のメタデータを修復する
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
                
                // 1. FolderPathがキャッシュディレクトリのGMAファイルを指している場合
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
                
                // 2. FolderPathが通常のディレクトリを指しているが、実際にはキャッシュにGMAファイルが存在する場合
                if (!addon.IsGmaFile && !string.IsNullOrEmpty(addon.FolderPath) && !addon.FolderPath.EndsWith(".gma"))
                {
                    // キャッシュ内のGMAファイルをチェック
                    string gmaPath = null;
                    
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
                        // GMAファイルが存在する場合、メタデータを修正
                        addon.FolderPath = gmaPath;
                        addon.IsGmaFile = true;
                        needsUpdate = true;
                        errorHandler.HandleInfo($"Fixed path and IsGmaFile flag for {addonId}", "RepairAddonMetadata");
                    }
                }
                
                // 3. IsGmaFileがtrueだが、FolderPathが正しくない場合
                if (addon.IsGmaFile)
                {
                    // 正しいGMAファイルパスを探す
                    string correctGmaPath = null;
                    
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
                
                // 4. 通常のアドオンのFolderPathチェック
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
        /// 重複アドオンをクリーンアップする（同じIDでディレクトリとGMAの両方が存在する場合）
        /// </summary>
        public async Task CleanupDuplicateAddonsAsync()
        {
            errorHandler.HandleInfo("Starting duplicate addon cleanup...", "CleanupDuplicateAddons");
            
            var duplicatesFound = new List<string>();
            var cleanupOperations = new List<(string addonId, string action)>();
            
            // 1. 同じアドオンIDで複数の形式が存在するケースを検出
            var addonGroups = configuration.AddonMetadata
                .GroupBy(kvp => kvp.Key)
                .Where(g => g.Count() > 1)
                .ToList();
            
            // 実際には、configuration.AddonMetadataは辞書なので重複キーはないが、
            // 同じアドオンIDでディレクトリとGMAの両方が存在する場合を検出する
            foreach (var kvp in configuration.AddonMetadata.ToList())
            {
                var addonId = kvp.Key;
                var addon = kvp.Value;
                
                // ディレクトリ形式のアドオンの場合
                if (!addon.IsGmaFile)
                {
                    // 同じIDのGMAファイルが存在するかチェック
                    string? gmaPath = null;
                    
                    // キャッシュフォルダ内のGMAファイルをチェック
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
                    
                    // GMAファイルが存在する場合は重複として検出
                    if (gmaPath != null)
                    {
                        duplicatesFound.Add(addonId);
                        
                        // 2. GMA形式を優先し、ディレクトリ形式をバックアップまたは削除
                        string directoryPath = addon.FolderPath;
                        if (!string.IsNullOrEmpty(directoryPath) && Directory.Exists(directoryPath))
                        {
                            try
                            {
                                // ジャンクションを削除（有効化されている場合）
                                if (addon.IsEnabled)
                                {
                                    string junctionPath = Path.Combine(workshopPath, addonId);
                                    if (Directory.Exists(junctionPath) && junctionService.IsJunction(junctionPath))
                                    {
                                        junctionService.RemoveJunction(junctionPath);
                                    }
                                }
                                
                                // ディレクトリを削除
                                Directory.Delete(directoryPath, true);
                                cleanupOperations.Add((addonId, $"Deleted duplicate directory"));
                                
                                errorHandler.HandleInfo($"Deleted duplicate directory addon {addonId}", "CleanupDuplicateAddons");
                                
                                // 3. メタデータを更新（GMA形式に切り替え）
                                addon.IsGmaFile = true;
                                addon.FolderPath = gmaPath;
                                
                                // GMAファイルから最新のメタデータを読み込む
                                ReadGmaMetadata(gmaPath, addon);
                                
                                // GMA形式のアドオンを有効化（元々有効だった場合）
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
        /// 既存のジャンクションから新しいハードリンク方式へ移行
        /// </summary>
        public async Task MigrateToHardLinkSystemAsync()
        {
            errorHandler.HandleInfo("Starting migration from junction to hard link system...", "MigrateToHardLinkSystem");
            
            int migratedCount = 0;
            int failedCount = 0;
            var failedAddons = new List<string>();
            
            // 全てのアドオンをチェック
            foreach (var kvp in configuration.AddonMetadata.ToList())
            {
                var addonId = kvp.Key;
                var addon = kvp.Value;
                
                // GMAファイルアドオンはスキップ
                if (addon.IsGmaFile)
                {
                    continue;
                }
                
                string workshopAddonPath = Path.Combine(workshopPath, addonId);
                string sourcePath = Path.Combine(addonsPath, addonId);
                string sourceGmaPath = Path.Combine(sourcePath, $"{addonId}.gma");
                
                // ジャンクションが存在し、かつGMAファイルが管理フォルダに存在する場合
                if (Directory.Exists(workshopAddonPath) && 
                    junctionService.IsJunction(workshopAddonPath) && 
                    File.Exists(sourceGmaPath))
                {
                    try
                    {
                        // 1. 既存のジャンクションを削除
                        junctionService.RemoveJunction(workshopAddonPath);
                        
                        // 2. 新方式でアドオンを有効化
                        junctionService.CreateWorkshopAddonStructure(workshopPath, addonId, sourceGmaPath);
                        
                        migratedCount++;
                        errorHandler.HandleInfo($"Migrated addon {addonId} to hard link system", "MigrateToHardLinkSystem");
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        failedAddons.Add(addonId);
                        errorHandler.HandleError(ex, $"Failed to migrate addon {addonId}", ErrorSeverity.Warning);
                        
                        // 失敗した場合はジャンクションを復元
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
            
            // 結果のサマリーを表示
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
        /// Workshopから削除されたアドオンをクリーンアップ
        /// </summary>
        private async Task<HashSet<string>> CleanupDeletedWorkshopAddonsAsync(List<WorkshopAddon> currentAddons)
        {
            var deletedAddonIds = new HashSet<string>();
            try
            {
                var existingAddonIds = new HashSet<string>(currentAddons.Select(a => a.Id));
                var toRemove = new List<string>();
                
                // メタデータに存在するが、実際のファイルが存在しないアドオンを検出
                foreach (var kvp in configuration.AddonMetadata)
                {
                    var addonId = kvp.Key;
                    var addon = kvp.Value;
                    
                    bool fileExists = false;
                    
                    if (addon.IsGmaFile)
                    {
                        // GMAファイルの存在確認
                        string managedGmaPath = Path.Combine(addonsPath, $"{addonId}.gma");
                        string cacheGmaPath = !string.IsNullOrEmpty(gmodCachePath) ? 
                            Path.Combine(gmodCachePath, $"{addonId}.gma") : null;
                        string cacheManagerGmaPath = !string.IsNullOrEmpty(gmodCacheAddonsPath) ? 
                            Path.Combine(gmodCacheAddonsPath, $"{addonId}.gma") : null;
                        
                        fileExists = File.Exists(managedGmaPath) || 
                                   (cacheGmaPath != null && File.Exists(cacheGmaPath)) ||
                                   (cacheManagerGmaPath != null && File.Exists(cacheManagerGmaPath));
                    }
                    else
                    {
                        // ディレクトリの存在確認
                        string managedDirPath = Path.Combine(addonsPath, addonId);
                        string workshopDirPath = Path.Combine(workshopPath, addonId);
                        
                        // 管理フォルダが存在する場合、空でないかチェック
                        if (Directory.Exists(managedDirPath))
                        {
                            var hasFiles = Directory.GetFiles(managedDirPath, "*", SearchOption.AllDirectories).Any();
                            if (!hasFiles)
                            {
                                // 空フォルダは削除
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
                            fileExists = Directory.Exists(workshopDirPath) && !junctionService.IsJunction(workshopDirPath);
                        }
                    }
                    
                    if (!fileExists)
                    {
                        toRemove.Add(addonId);
                        errorHandler.HandleInfo($"Detected deleted workshop addon: {addonId}", "CleanupDeletedWorkshopAddons");
                    }
                }
                
                // 削除されたアドオンをクリーンアップ
                foreach (var addonId in toRemove)
                {
                    // メタデータから削除
                    configuration.AddonMetadata.Remove(addonId);
                    
                    // 全てのアセットから削除（Subscribeアセットを含む）
                    foreach (var asset in configuration.Assets)
                    {
                        if (asset.ContainsAllAddons() || asset.Addons.Contains(addonId))
                        {
                            asset.RemoveAddon(addonId);
                            // アセットの状態からも削除
                            if (asset.AddonStates.ContainsKey(addonId))
                            {
                                asset.AddonStates.Remove(addonId);
                            }
                        }
                    }
                    
                    // ジャンクション履歴から削除
                    configuration.JunctionHistory.Remove(addonId);
                    
                // Workshop配下の構造は変更しない（Steamの検証・再DL誘発を避ける）
                    
                    // 管理フォルダからアドオンファイルを削除
                    try
                    {
                        // ディレクトリタイプのアドオン
                        string managedDirPath = Path.Combine(addonsPath, addonId);
                        if (Directory.Exists(managedDirPath))
                        {
                            Directory.Delete(managedDirPath, true);
                            errorHandler.HandleInfo($"Deleted managed directory: {managedDirPath}", "CleanupDeletedWorkshopAddons");
                        }
                        
                        // GMAファイルタイプのアドオン
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
                    
                    // サムネイルキャッシュを削除
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
                    
                    // 削除されたアドオンIDを返す
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
        /// Steamでサブスクライブ解除されたアドオンを検出して削除
        /// </summary>
        private async Task CleanupUnsubscribedAddonsAsync()
        {
            var toDelete = new List<string>();
            
            errorHandler.HandleInfo("Checking for unsubscribed addons...", "CleanupUnsubscribedAddons");
            
            foreach (var kvp in configuration.AddonMetadata)
            {
                var addonId = kvp.Key;
                var addon = kvp.Value;
                var workshopPath = Path.Combine(this.workshopPath, addonId);
                
                // ワークショップフォルダ側が完全に存在しない（ジャンクションも実体もGMAファイルも無い）
                bool workshopExists = Directory.Exists(workshopPath) || File.Exists(workshopPath) || 
                                    File.Exists(workshopPath + ".gma");
                
                if (!workshopExists)
                {
                    // 管理フォルダ側に実体が存在するかチェック
                    bool managedExists = false;
                    
                    if (addon.IsGmaFile)
                    {
                        // GMAファイルの場合
                        string managedGmaPath = Path.Combine(addonsPath, $"{addonId}.gma");
                        string cacheGmaPath = !string.IsNullOrEmpty(gmodCachePath) ? 
                            Path.Combine(gmodCachePath, $"{addonId}.gma") : null;
                        string cacheManagerGmaPath = !string.IsNullOrEmpty(gmodCacheAddonsPath) ? 
                            Path.Combine(gmodCacheAddonsPath, $"{addonId}.gma") : null;
                        
                        managedExists = File.Exists(managedGmaPath) || 
                                      (cacheGmaPath != null && File.Exists(cacheGmaPath)) ||
                                      (cacheManagerGmaPath != null && File.Exists(cacheManagerGmaPath));
                    }
                    else
                    {
                        // ディレクトリタイプの場合
                        string managedDirPath = Path.Combine(addonsPath, addonId);
                        managedExists = Directory.Exists(managedDirPath);
                    }
                    
                    if (managedExists)
                    {
                        // ワークショップ側に無いが管理フォルダに存在 = サブスクライブ解除された
                        toDelete.Add(addonId);
                        errorHandler.HandleInfo($"Detected unsubscribed addon: {addonId}", "CleanupUnsubscribedAddons");
                    }
                }
            }
            
            // 検出されたアドオンを削除
            foreach (var addonId in toDelete)
            {
                try
                {
                    // 管理フォルダから削除
                    if (configuration.AddonMetadata[addonId].IsGmaFile)
                    {
                        // GMAファイルを削除
                        string managedGmaPath = Path.Combine(addonsPath, $"{addonId}.gma");
                        if (File.Exists(managedGmaPath))
                        {
                            File.Delete(managedGmaPath);
                            errorHandler.HandleInfo($"Deleted managed GMA file: {managedGmaPath}", "CleanupUnsubscribedAddons");
                        }
                        
                        // キャッシュからも削除
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
                        // ディレクトリを削除
                        string managedDirPath = Path.Combine(addonsPath, addonId);
                        if (Directory.Exists(managedDirPath))
                        {
                            Directory.Delete(managedDirPath, true);
                            errorHandler.HandleInfo($"Deleted managed directory: {managedDirPath}", "CleanupUnsubscribedAddons");
                        }
                    }
                    
                    // メタデータから削除
                    configuration.AddonMetadata.Remove(addonId);
                    
                    // 全てのアセットから削除
                    foreach (var asset in configuration.Assets)
                    {
                        if (asset.ContainsAllAddons() || asset.Addons.Contains(addonId))
                        {
                            asset.RemoveAddon(addonId);
                        }
                    }
                    
                    // ジャンクション履歴から削除
                    configuration.JunctionHistory.Remove(addonId);
                    
                    // サムネイルキャッシュを削除
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
        /// キャッシュ管理の整合性チェックと修復
        /// </summary>
        public async Task RepairCacheManagementAsync()
        {
            if (string.IsNullOrEmpty(gmodCachePath))
                return;

            errorHandler.HandleInfo("Starting cache management repair...", "RepairCacheManagement");

            // 管理フォルダが存在しない場合は作成
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

            // 管理されていないGMAファイルを検出して移行
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
                        
                        // 管理フォルダに移動
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
                        
                        // 有効状態を維持するためハードリンクを作成
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
            if (File.Exists(cacheGmaPath))
                return true;
                
            // Check cache directory for .cache file (Garry's Mod sometimes uses .cache extension)
            string cacheCachePath = Path.Combine(gmodCachePath, $"{addonId}.cache");
            if (File.Exists(cacheCachePath) && LooksLikeGmaFile(cacheCachePath))
                return true;
                
            // Check managed cache directory for GMA file
            if (!string.IsNullOrEmpty(gmodCacheAddonsPath))
            {
                string managedGmaPath = Path.Combine(gmodCacheAddonsPath, $"{addonId}.gma");
                if (File.Exists(managedGmaPath))
                    return true;
            }

            // Check workshop manager directory for GMA file
            string managedWorkshopGmaPath = Path.Combine(addonsPath, addonId, $"{addonId}.gma");
            if (File.Exists(managedWorkshopGmaPath))
                return true;
            
            // Legacy managed GMA location
            string legacyManagedWorkshopGmaPath = Path.Combine(addonsPath, $"{addonId}.gma");
            if (File.Exists(legacyManagedWorkshopGmaPath))
                return true;
            
            // Check workshop directory for GMA file structure
            string workshopAddonPath = Path.Combine(workshopPath, addonId);
            if (Directory.Exists(workshopAddonPath))
            {
                string workshopGmaPath = Path.Combine(workshopAddonPath, $"{addonId}.gma");
                if (File.Exists(workshopGmaPath))
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
        
        /// <summary>
        /// Workshopからアドオンをサブスクライブする
        /// </summary>
        private async Task SubscribeToWorkshopAsync(string addonId)
        {
            try
            {
                // Steam URLスキームを使用してサブスクライブ
                var url = $"steam://subscribe/4000/{addonId}";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
                
                // 少し待機（Steam処理のため）
                await Task.Delay(100);
                
                errorHandler.HandleInfo($"Subscribed to workshop addon: {addonId}", "WorkshopSubscribe");
            }
            catch (Exception ex)
            {
                errorHandler.HandleError(ex, $"Failed to subscribe to workshop addon {addonId}", ErrorSeverity.Warning);
            }
        }
        
        /// <summary>
        /// Workshopからアドオンのサブスクライブを解除する
        /// </summary>
        private async Task UnsubscribeFromWorkshopAsync(string addonId)
        {
            try
            {
                // Steam URLスキームを使用してサブスクライブ解除
                var url = $"steam://unsubscribe/4000/{addonId}";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
                
                // 少し待機（Steam処理のため）
                await Task.Delay(100);
                
                errorHandler.HandleInfo($"Unsubscribed from workshop addon: {addonId}", "WorkshopUnsubscribe");
            }
            catch (Exception ex)
            {
                errorHandler.HandleError(ex, $"Failed to unsubscribe from workshop addon {addonId}", ErrorSeverity.Warning);
            }
        }
    }
}
