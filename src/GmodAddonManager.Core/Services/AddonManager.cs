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
        private readonly IReadOnlyList<string>? customWorkshopCacheFilePaths;
        private readonly AsyncLocal<LinkOperationMetrics?> linkMetricsContext = new AsyncLocal<LinkOperationMetrics?>();

        private readonly System.Threading.Timer _saveDebounceTimer;
        private readonly object _saveLock = new object();
        private bool _saveRequested = false;
        private int _saveDebounceMilliseconds = 1000; // 繝・ヵ繧ｩ繝ｫ繝・遘・
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
            var appDataPath = !string.IsNullOrWhiteSpace(options.CustomAppDataPath)
                ? options.CustomAppDataPath
                : Path.Combine(
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
            customWorkshopCacheFilePaths = options.CustomWorkshopCacheFilePaths;
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
            }            // Detect Gmod cache path
            if (options.DisableCacheScan)
            {
                gmodCachePath = null;
                errorHandler.HandleInfo("Cache scanning disabled by options", "Constructor");
            }
            else
            {
                try
                {
                    gmodCachePath = steamPathDetector.DetectGmodCachePath();
                    // Detected gmodCachePath
                }
                catch (Exception ex)
                {
                    // Error detecting cache path
                    // ????????????????ull?????????
                    gmodCachePath = null;
                }
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
                var candidate = Path.GetFullPath(Path.Combine(workshopPath, @"..\..\..\common\GarrysMod"));
                if (Directory.Exists(candidate))
                {
                    gmodRootPath = candidate;
                    gmodAddonStateStore = new GmodAddonStateStore(gmodRootPath);
                    errorHandler.HandleInfo($"Set gmodRootPath: {gmodRootPath}", "Constructor");
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

            // 繝・ヰ繧ｦ繝ｳ繧ｹ繧ｿ繧､繝槭・縺ｮ蛻晄悄蛹・
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

                // 謫堺ｽ懊Ο繧ｰ繝槭ロ繝ｼ繧ｸ繝｣繝ｼ繧貞・譛溷喧
                operationLogManager = new OperationLogManager(managerPath);

                // 襍ｷ蜍墓凾縺ｫ蜿､縺・Ο繧ｰ繧偵け繝ｪ繝ｼ繝ｳ繧｢繝・・
                operationLogManager.CleanupOldLogs();

                if (File.Exists(configPath))
                {
                    await LoadConfigurationAsync();

                    // 繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ繧｢繧ｻ繝・ヨ縺悟ｭ伜惠縺励↑縺・ｴ蜷医・霑ｽ蜉
                    EnsureJunctionAssetExists();
                }
                else
                {
                    configuration = new Configuration();
                    configuration.CreateDefaultAssets();
                    await SaveConfigurationAsync();
                }

                // 襍ｷ蜍墓凾縺ｫ譛ｪ螳御ｺ・・謫堺ｽ懊ｒ繝√ぉ繝・け
                await CheckIncompleteOperationsAsync();

                await MigrateExistingAddonsAsync();

                // 襍ｷ蜍墓凾縺ｮ繧ｷ繧ｹ繝・Β謨ｴ蜷域ｧ繝√ぉ繝・け
                await modeStrategy.ValidateSystemIntegrityAsync(this);

                // 繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ迥ｶ諷九・繧｢繝峨が繝ｳ繧呈､懷・縺励※譖ｴ譁ｰ
                await UpdateJunctionAssetAsync();

                // Subscribe繧｢繧ｻ繝・ヨ縺ｫ蜈ｨ繧｢繝峨が繝ｳ縺悟性縺ｾ繧後※縺・ｋ縺薙→繧堤｢ｺ隱・
                await EnsureAllAddonsInSubscribeAssetAsync();

                // 蛻晄悄蛹門ｾ後∝・繧｢繝峨が繝ｳ縺ｮ迥ｶ諷九ｒ遒ｺ螳溘↓譖ｴ譁ｰ
                await UpdateAddonStatesAsync();

                // 譛蠕後↓繧ｵ繝悶せ繧ｯ繝ｩ繧､繝冶ｧ｣髯､縺輔ｌ縺溘い繝峨が繝ｳ繧偵け繝ｪ繝ｼ繝ｳ繧｢繝・・
                // UpdateAddonStatesAsync縺ｮ蠕後↓螳溯｡後☆繧九％縺ｨ縺ｧ縲√ず繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ蜀堺ｽ懈・繧帝亟縺・
                await modeStrategy.CleanupUnsubscribedAddonsAsync(this);

                // Addon Manager initialization complete
            }
            catch (Exception ex)
            {
                throw; // 繧ｨ繝ｩ繝ｼ繧貞・繧ｹ繝ｭ繝ｼ縺励※縲∝他縺ｳ蜃ｺ縺怜・縺ｧ蜃ｦ逅・〒縺阪ｋ繧医≧縺ｫ縺吶ｋ
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

            // 蜈ｨ縺ｦ縺ｮ繧｢繝峨が繝ｳ・医ヵ繧ｩ繝ｫ繝縺ｨGMA縺ｮ荳｡譁ｹ・峨ｒSubscribe繧｢繧ｻ繝・ヨ縺ｫ霑ｽ蜉
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
                    // 繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ繧｢繧ｻ繝・ヨ縺ｫ螻槭☆繧九い繝峨が繝ｳ縺ｯ髯､螟・
                    var junctionAsset = configuration.Assets.FirstOrDefault(a => a.Id == "junction-system-asset");
                    if (junctionAsset != null && junctionAsset.Addons.Contains(kvp.Key))
                    {
                        continue;
                    }

                    subscribeAsset.AddAddon(kvp.Key, kvp.Value.IsEnabled ? AddonState.Enabled : AddonState.Disabled);
                    needsSave = true;
                }
            }

            // 繝｡繧ｿ繝・・繧ｿ縺御ｿｮ豁｣縺輔ｌ縺溷ｴ蜷医・菫晏ｭ・
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
                        // 螳滉ｽ薙ヵ繧ｩ繝ｫ繝縺ｮ蝣ｴ蜷医∫ｮ｡逅・ヵ繧ｩ繝ｫ繝縺ｫ遘ｻ蜍包ｼ亥､ｱ謨玲凾縺ｯ繝ｭ繝ｼ繝ｫ繝舌ャ繧ｯ・・
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

                        // GMA繝輔ぃ繧､繝ｫ縺悟ｭ伜惠縺吶ｋ縺九メ繧ｧ繝・け・亥ｭ伜惠縺吶ｋ蝣ｴ蜷医・繝上・繝峨Μ繝ｳ繧ｯ譁ｹ蠑上ｒ蜆ｪ蜈茨ｼ・
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

                        // 譌｢縺ｫ邂｡逅・ヵ繧ｩ繝ｫ繝縺悟ｭ伜惠縺励※縺・◆蝣ｴ蜷医・縺ｿ縲∵ｮ九▲縺ｦ縺・ｋ螳滉ｽ薙ヵ繧ｩ繝ｫ繝繧偵・繝ｼ繧ｸ
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
                                // 繝ｭ繝ｼ繝ｫ繝舌ャ繧ｯ縺ｯ髮｣縺励＞縺ｮ縺ｧ縲√ユ繝ｳ繝昴Λ繝ｪ繧呈ｮ九＠縺ｦ隴ｦ蜻翫↓逡吶ａ繧・
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
                // 繝輔か繝ｫ繝縺悟ｭ伜惠縺励↑縺・ｴ蜷医・菴懈・繧定ｩｦ縺ｿ繧・
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
                        return; // GMA遘ｻ陦後ｒ繧ｹ繧ｭ繝・・
                    }
                }

                // 繝輔か繝ｫ繝縺梧ｭ｣蟶ｸ縺ｫ蟄伜惠縺吶ｋ蝣ｴ蜷医・縺ｿ蜃ｦ逅・ｒ邯夊｡・
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

                            // 繧ｿ繧､繝医Ν縺悟叙蠕励〒縺阪↑縺九▲縺溷ｴ蜷医∬､・焚蝗槫・隧ｦ陦・
                            int retryCount = 0;
                            while ((string.IsNullOrWhiteSpace(addon.Title) || addon.Title == fileName || addon.Title.StartsWith("Workshop-")) && retryCount < 3)
                            {
                                await Task.Delay(100 * (retryCount + 1)); // 蠕舌・↓驕・ｻｶ繧貞｢励ｄ縺・
                                var title = await ReadGmaTitleOnlyAsync(targetPath);
                                if (!string.IsNullOrWhiteSpace(title) && title != fileName && !title.StartsWith("Workshop-"))
                                {
                                    addon.Title = title;
                                    break;
                                }
                                retryCount++;
                            }

                            // 縺昴ｌ縺ｧ繧ょ叙蠕励〒縺阪↑縺・ｴ蜷医・縲仝orkshop蠖｢蠑上・繧ｿ繧､繝医Ν繧堤ｶｭ謖・
                            if (string.IsNullOrWhiteSpace(addon.Title) || addon.Title == fileName)
                            {
                                addon.Title = $"Workshop-{fileName}";
                                addon.NeedsTitleUpdate = true; // 繧ｿ繧､繝医Ν譖ｴ譁ｰ縺悟ｿ・ｦ√↑繝輔Λ繧ｰ繧堤ｫ九※繧・
                            }
                            else if (addon.Title.StartsWith("Workshop-"))
                            {
                                // 縺吶〒縺ｫWorkshop蠖｢蠑上・蝣ｴ蜷医ｂ繧ｿ繧､繝医Ν譖ｴ譁ｰ縺悟ｿ・ｦ・
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

                            // 譌｢蟄倥い繝峨が繝ｳ縺ｧ繧ゅち繧､繝医Ν縺御ｸ埼←蛻・↑蝣ｴ蜷医・譖ｴ譁ｰ
                            if (existingAddon.Title == fileName || existingAddon.Title.StartsWith("Workshop-") || existingAddon.Title.StartsWith("Cache Addon") || existingAddon.NeedsTitleUpdate)
                            {
                                // 隍・焚蝗槫・隧ｦ陦・
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

                                // 縺昴ｌ縺ｧ繧ょ叙蠕励〒縺阪↑縺・ｴ蜷・
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

            // 1. 邂｡逅・ヵ繧ｩ繝ｫ繝縺ｮ繧｢繝峨が繝ｳ繧偵せ繧ｭ繝｣繝ｳ
            if (Directory.Exists(addonsPath))
            {
                var directories = Directory.GetDirectories(addonsPath);

                foreach (var directory in directories)
                {
                    string addonId = Path.GetFileName(directory);
                    processedIds.Add(addonId);


                    // 菫晏ｭ倥＆繧後※縺・ｋ繝｡繧ｿ繝・・繧ｿ縺後≠繧句ｴ蜷医・蜆ｪ蜈育噪縺ｫ菴ｿ逕ｨ
                    if (configuration?.AddonMetadata != null && configuration.AddonMetadata.ContainsKey(addonId))
                    {
                        var savedAddon = configuration.AddonMetadata[addonId];
                        // 繝輔か繝ｫ繝繝代せ縺ｨ譛牙柑迥ｶ諷九ｒ譖ｴ譁ｰ
                        savedAddon.FolderPath = directory;
                        // IsGmaFile縺ｯ繝｡繧ｿ繝・・繧ｿ縺九ｉ菫晄戟縺吶ｋ縺九∝ｮ滄圀縺ｮ繝輔ぃ繧､繝ｫ繧偵メ繧ｧ繝・け
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
                        // 繝｡繧ｿ繝・・繧ｿ縺後↑縺・ｴ蜷医・譁ｰ隕上せ繧ｭ繝｣繝ｳ
                        var addon = await ScanAddonAsync(directory);
                        if (addon != null)
                        {
                            // 螳滄圀縺ｮ繝輔ぃ繧､繝ｫ繧偵メ繧ｧ繝・け縺励※GMA縺九←縺・°蛻､螳・
                            addon.IsGmaFile = IsGmaAddonRuntime(addonId);
                            // 譁ｰ縺励＞繧｢繝峨が繝ｳ縺ｮ繝｡繧ｿ繝・・繧ｿ繧剃ｿ晏ｭ・
                            configuration.AddonMetadata[addonId] = addon;
                            addons.Add(addon);
                        }
                    }
                }
            }

            // 2. 繝｡繧ｿ繝・・繧ｿ縺ｫ菫晏ｭ倥＆繧後※縺・ｋ縺後√∪縺蜃ｦ逅・＆繧後※縺・↑縺・い繝峨が繝ｳ・井ｸｻ縺ｫGMA繝輔ぃ繧､繝ｫ・峨ｒ霑ｽ蜉
            if (configuration?.AddonMetadata != null)
            {
                foreach (var kvp in configuration.AddonMetadata)
                {
                    if (!processedIds.Contains(kvp.Key))
                    {
                        // 繧｢繝峨が繝ｳ縺悟ｮ滄圀縺ｫ蟄伜惠縺吶ｋ縺狗｢ｺ隱・
                        bool addonExists = false;

                        // GMA繝輔ぃ繧､繝ｫ縺ｮ譛牙柑迥ｶ諷九ｒ譖ｴ譁ｰ
                        if (kvp.Value.IsGmaFile)
                        {
                            string gmaPath = null;

                            // 邂｡逅・ヵ繧ｩ繝ｫ繝繧堤｢ｺ隱・
                            string managedGmaPath = Path.Combine(addonsPath, $"{kvp.Key}.gma");
                            if (File.Exists(managedGmaPath))
                            {
                                gmaPath = managedGmaPath;
                                addonExists = true;
                            }

                            // 繧ｭ繝｣繝・す繝･繝槭ロ繝ｼ繧ｸ繝｣繝ｼ繝代せ繧堤｢ｺ隱・
                            if (!addonExists && !string.IsNullOrEmpty(gmodCacheAddonsPath))
                            {
                                gmaPath = Path.Combine(gmodCacheAddonsPath, $"{kvp.Key}.gma");
                                if (File.Exists(gmaPath))
                                {
                                    addonExists = true;
                                }
                                else
                                {
                                    // 繧ｭ繝｣繝・す繝･繝代せ繧堤｢ｺ隱・
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
                            // 繝・ぅ繝ｬ繧ｯ繝医Μ繧ｿ繧､繝励・繧｢繝峨が繝ｳ
                            string managedDirPath = Path.Combine(addonsPath, kvp.Key);
                            if (Directory.Exists(managedDirPath))
                            {
                                addonExists = true;
                            }
                        }

                        // 蟄伜惠縺吶ｋ繧｢繝峨が繝ｳ縺ｮ縺ｿ霑ｽ蜉
                        if (addonExists)
                        {
                            addons.Add(kvp.Value);
                            processedIds.Add(kvp.Key);
                        }
                    }
                }
            }

            // 3. 繧ｭ繝｣繝・す繝･繝・ぅ繝ｬ繧ｯ繝医Μ縺ｮGMA繝輔ぃ繧､繝ｫ繧偵せ繧ｭ繝｣繝ｳ
            errorHandler.HandleInfo($"Cache scan check - gmodCacheAddonsPath: {gmodCacheAddonsPath ?? "null"}, Exists: {(!string.IsNullOrEmpty(gmodCacheAddonsPath) && Directory.Exists(gmodCacheAddonsPath))}", "ScanWorkshopFolderAsync");

            if (!string.IsNullOrEmpty(gmodCacheAddonsPath) && Directory.Exists(gmodCacheAddonsPath))
            {
                errorHandler.HandleInfo($"Scanning cache directory: {gmodCacheAddonsPath}", "ScanWorkshopFolderAsync");
                var gmaFiles = Directory.GetFiles(gmodCacheAddonsPath, "*.gma");

                foreach (var gmaFile in gmaFiles)
                {
                    string addonId = Path.GetFileNameWithoutExtension(gmaFile);

                    // 縺吶〒縺ｫ蜃ｦ逅・ｸ医∩縺ｮ蝣ｴ蜷医・繧ｹ繧ｭ繝・・
                    if (processedIds.Contains(addonId))
                        continue;

                    processedIds.Add(addonId);

                    // 繝｡繧ｿ繝・・繧ｿ縺後≠繧句ｴ蜷医・菴ｿ逕ｨ
                    if (configuration?.AddonMetadata != null && configuration.AddonMetadata.ContainsKey(addonId))
                    {
                        var savedAddon = configuration.AddonMetadata[addonId];
                        savedAddon.FolderPath = gmaFile;
                        savedAddon.IsGmaFile = true;  // 蠢・★GMA繝輔ぃ繧､繝ｫ縺ｨ縺励※繝槭・繧ｯ
                        savedAddon.IsEnabled = File.Exists(gmaFile);

                        // 繝｡繧ｿ繝・・繧ｿ繧呈峩譁ｰ縺励※菫晏ｭ・
                        configuration.AddonMetadata[addonId] = savedAddon;

                        addons.Add(savedAddon);
                    }
                    else
                    {
                        // 譁ｰ隕秀MA繝輔ぃ繧､繝ｫ縺ｮ蝣ｴ蜷・
                        var addon = new WorkshopAddon(addonId, gmaFile);
                        addon.IsGmaFile = true;
                        addon.IsEnabled = true;

                        // GMA繝輔ぃ繧､繝ｫ縺九ｉ繝｡繧ｿ繝・・繧ｿ繧定ｪｭ縺ｿ蜿悶ｋ
                        ReadGmaMetadata(gmaFile, addon);

                        if (string.IsNullOrWhiteSpace(addon.Title))
                        {
                            addon.Title = $"Workshop-{addonId}";
                        }

                        var fileInfo = new FileInfo(gmaFile);
                        addon.Size = fileInfo.Length;
                        addon.LastUpdated = fileInfo.LastWriteTimeUtc;

                        // 繝｡繧ｿ繝・・繧ｿ縺ｫ菫晏ｭ・
                        if (configuration != null)
                        {
                            configuration.AddonMetadata[addonId] = addon;
                        }

                        addons.Add(addon);
                    }
                }

                errorHandler.HandleInfo($"Found {gmaFiles.Length} GMA files in cache directory", "ScanWorkshopFolderAsync");
            }

            // 4. Workshop縺九ｉ蜑企勁縺輔ｌ縺溘い繝峨が繝ｳ縺ｮ繧ｯ繝ｪ繝ｼ繝ｳ繧｢繝・・
            var deletedAddonIds = await CleanupDeletedWorkshopAddonsAsync(addons);

            // 蜑企勁縺輔ｌ縺溘い繝峨が繝ｳ繧偵Μ繧ｹ繝医°繧蛾勁螟・
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

                    if (!DirectoryHasAddonPayload(directory, "ScanWorkshopFolderSoftAsync"))
                    {
                        errorHandler.HandleInfo($"Skipping empty workshop directory: {directory}", "ScanWorkshopFolderSoftAsync");
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
                        if (DirectoryHasAddonPayload(workshopDirPath, "ScanWorkshopFolderSoftAsync"))
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

            var deletedAddonIds = await CleanupDeletedWorkshopAddonsAsync(addons);
            if (deletedAddonIds.Count > 0)
            {
                addons = addons.Where(a => !deletedAddonIds.Contains(a.Id)).ToList();
            }

            await EnsureAllAddonsInSubscribeAssetAsync();

            return addons;
        }

        public async Task RegisterNewAddonsAsync(IEnumerable<WorkshopAddon> newAddons)
        {
            var newAddonList = newAddons
                .Where(addon => addon != null && !string.IsNullOrWhiteSpace(addon.Id))
                .GroupBy(addon => addon.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();

            if (newAddonList.Count == 0)
            {
                return;
            }

            foreach (var addon in newAddonList)
            {
                configuration.AddonMetadata[addon.Id] = addon;
            }

            var newAddonIds = new HashSet<string>(newAddonList.Select(addon => addon.Id), StringComparer.Ordinal);
            await MigrateExistingAddonsAsync(newAddonIds);
            await EnsureAllAddonsInSubscribeAssetAsync();
            await SaveConfigurationAsync();
        }

        private List<string> GetSubscribedAddonIdsFromCache()
        {
            return customWorkshopCacheFilePaths != null
                ? SteamWorkshopCacheReader.GetSubscribedAddonIds(customWorkshopCacheFilePaths)
                : SteamWorkshopCacheReader.GetSubscribedAddonIds();
        }

        private Dictionary<string, WorkshopItemInfo> GetAddonDetailsFromCache()
        {
            return customWorkshopCacheFilePaths != null
                ? SteamWorkshopCacheReader.GetAddonDetails(customWorkshopCacheFilePaths)
                : SteamWorkshopCacheReader.GetAddonDetails();
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
                var workshopCacheIds = GetSubscribedAddonIdsFromCache();
                if (workshopCacheIds.Any())
                {
                    errorHandler.HandleInfo($"Found {workshopCacheIds.Count} addon IDs in Steam Workshop cache", "ScanForNewAddonsAsync");
                    cachedAddonIds = new HashSet<string>(workshopCacheIds, StringComparer.Ordinal);
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
            var downloadedAddonIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var dir in workshopDirs)
            {
                string dirName = Path.GetFileName(dir);

                // Check if it's a valid addon ID
                if (!long.TryParse(dirName, out _))
                    continue;

                if (!DirectoryHasAddonPayload(dir, "ScanForNewAddonsAsync"))
                {
                    errorHandler.HandleInfo($"Skipping empty workshop directory: {dir}", "ScanForNewAddonsAsync");
                    continue;
                }

                downloadedAddonIds.Add(dirName);

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
                    IsGmaFile = IsGmaAddonRuntime(dirName) // 螳滄圀縺ｮ繝輔ぃ繧､繝ｫ繧偵メ繧ｧ繝・け
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
                var missingAddonIds = cachedAddonIds
                    .Except(downloadedAddonIds)
                    .Where(id => !configuration.AddonMetadata.ContainsKey(id))
                    .ToList();

                var cachedDetails = new Dictionary<string, WorkshopItemInfo>(StringComparer.Ordinal);
                try
                {
                    cachedDetails = GetAddonDetailsFromCache();
                }
                catch (Exception ex)
                {
                    errorHandler?.HandleInfo($"Failed to read Steam cache details: {ex.Message}", "ReadSteamCache");
                }

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
                    if (cachedDetails.TryGetValue(addonId, out var info))
                    {
                        if (!string.IsNullOrEmpty(info.Title))
                            addon.Title = info.Title;
                        if (info.TimeUpdated.HasValue)
                            addon.LastUpdated = info.TimeUpdated.Value;
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

            if (!DirectoryHasAddonPayload(addonPath, "ScanAddonAsync"))
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
                // GMA繝輔ぃ繧､繝ｫ縺後↑縺・ｴ蜷医・Steam API縺九ｉ繧ｿ繧､繝医Ν繧貞叙蠕励＠縺ｦ縺ｿ繧・
                // No GMA file found - will try Steam API
            }

            // 繧ｿ繧､繝医Ν縺檎ｩｺ縺ｮ蝣ｴ蜷医・Workshop ID繧剃ｽｿ逕ｨ
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
                    // 繝輔ぃ繧､繝ｫ縺梧怙蟆城剞縺ｮ繧ｵ繧､繧ｺ繧呈戟縺｣縺ｦ縺・ｋ縺狗｢ｺ隱・
                    if (stream.Length < 22) // GMAD(4) + version(1) + steamId(8) + timestamp(8) + requiredContentCount(1) = 22 bytes minimum
                    {
                        errorHandler.HandleWarning($"GMA file {gmaPath} is too small to be valid", "ReadGmaMetadata");
                        return;
                    }

                    // 繝槭ず繝・け繝翫Φ繝舌・繧偵ヰ繧､繝磯・蛻励→縺励※隱ｭ縺ｿ蜿悶ｊ
                    var magicBytes = reader.ReadBytes(4);
                    var magic = System.Text.Encoding.ASCII.GetString(magicBytes);
                    if (!magic.Equals("GMAD", StringComparison.Ordinal))
                    {
                        return;
                    }

                    byte version = reader.ReadByte();

                    ulong steamId = reader.ReadUInt64();
                    ulong timestamp = reader.ReadUInt64();

                    // 繧ｿ繧､繝繧ｹ繧ｿ繝ｳ繝励ｒDateTime縺ｫ螟画鋤
                    if (timestamp > 0)
                    {
                        addon.LastUpdated = DateTimeOffset.FromUnixTimeSeconds((long)timestamp).DateTime;
                    }

                    // Required content 縺ｮ蛟区焚繧定ｪｭ縺ｿ蜿悶ｊ縲∝推隕∫ｴ繧偵せ繧ｭ繝・・
                    byte requiredContentCount = reader.ReadByte();
                    for (int i = 0; i < requiredContentCount; i++)
                    {
                        ReadNullTerminatedString(reader); // 隱ｭ縺ｿ謐ｨ縺ｦ
                    }

                    string name = ReadNullTerminatedString(reader);
                    // 繧ｿ繧､繝医Ν縺檎洒縺吶℃繧句ｴ蜷医ｄ迚ｹ螳壹・繝励Ξ繝輔ぅ繝・け繧ｹ縺ｮ蝣ｴ蜷医・辟｡蜉ｹ縺ｨ縺ｿ縺ｪ縺・
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

            // 繧ｿ繧､繝医Ν縺檎ｩｺ縺ｮ蝣ｴ蜷医・GMA繝輔ぃ繧､繝ｫ蜷阪ｒ菴ｿ逕ｨ
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
                    // 螳牙・縺ｮ縺溘ａ縲∵枚蟄怜・縺碁聞縺吶℃繧句ｴ蜷医・荳ｭ譁ｭ
                    if (bytes.Count > 1024)
                    {
                        break;
                    }
                }
            }
            catch (EndOfStreamException)
            {
                // 繧ｹ繝医Μ繝ｼ繝縺ｮ邨らｫｯ縺ｫ驕斐＠縺溷ｴ蜷医・縲√％繧後∪縺ｧ縺ｫ隱ｭ縺ｿ蜿悶▲縺溘ヰ繧､繝医ｒ霑斐☆
                // GMA繝輔ぃ繧､繝ｫ縺御ｸ榊ｮ悟・縺ｪ蝣ｴ蜷医ｄ繝輔か繝ｼ繝槭ャ繝医′逡ｰ縺ｪ繧句ｴ蜷医↓逋ｺ逕溘☆繧句庄閭ｽ諤ｧ縺後≠繧・
            }
            return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
        }

        // GMA繝輔ぃ繧､繝ｫ縺九ｉ繧ｿ繧､繝医Ν縺ｮ縺ｿ繧帝ｫ倬溘↓隱ｭ縺ｿ蜿悶ｋ蟆ら畑繝｡繧ｽ繝・ラ
        private async Task<string> ReadGmaTitleOnlyAsync(string gmaPath)
        {
            try
            {
                // FileShare.Read縺ｧ莉悶・繝励Ο繧ｻ繧ｹ縺ｨ縺ｮ遶ｶ蜷医ｒ蝗樣∩
                using (var fs = new FileStream(gmaPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var br = new BinaryReader(fs, System.Text.Encoding.UTF8))
                {
                    // 繝輔ぃ繧､繝ｫ縺梧怙蟆城剞縺ｮ繧ｵ繧､繧ｺ繧呈戟縺｣縺ｦ縺・ｋ縺狗｢ｺ隱・
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
                        ReadNullTerminatedString(br); // 隱ｭ縺ｿ謐ｨ縺ｦ
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
                        // 繧ｹ繝医Μ繝ｼ繝縺ｮ邨らｫｯ縺ｫ驕斐＠縺溷ｴ蜷医・縲√％繧後∪縺ｧ縺ｫ隱ｭ縺ｿ蜿悶▲縺溘ヰ繧､繝医ｒ霑斐☆
                    }

                    var title = System.Text.Encoding.UTF8.GetString(bytes.ToArray());

                    // 繧ｿ繧､繝医Ν縺檎音螳壹・繝励Ξ繝輔ぅ繝・け繧ｹ縺ｮ蝣ｴ蜷医・辟｡蜉ｹ縺ｨ縺ｿ縺ｪ縺・
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

        // 繧ｭ繝｣繝・す繝･繧｢繝峨が繝ｳ縺ｮ蜷榊燕縺ｮ縺ｿ繧帝ｫ倬溘↓蜿門ｾ励☆繧区眠繝｡繧ｽ繝・ラ
        /// <summary>
        /// 繝舌ャ繧ｯ繧ｰ繝ｩ繧ｦ繝ｳ繝峨〒繧｢繝峨が繝ｳ縺ｮ繧ｿ繧､繝医Ν繧呈峩譁ｰ縺吶ｋ
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

                                    // 險ｭ螳壹ｒ菫晏ｭ・
                                    await SaveConfigurationAsync();
                                }
                            }
                        }
                    }
                    catch
                    {
                        // 繧ｨ繝ｩ繝ｼ縺ｯ辟｡隕悶＠縺ｦ邯夊｡・
                    }

                    // 蟆代＠蠕・ｩ溘＠縺ｦ雋闕ｷ繧貞・謨｣
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

            // 荳ｦ蛻怜・逅・〒繧ｿ繧､繝医Ν繧定ｪｭ縺ｿ蜿悶ｋ
            var titleTasks = cacheAddons.Select(async kvp =>
            {
                var addonId = kvp.Key;
                var addon = kvp.Value;

                // GMA繝輔ぃ繧､繝ｫ縺ｮ繝代せ繧呈ｧ狗ｯ・
                string gmaPath = null;

                // 縺ｾ縺壹く繝｣繝・す繝･繝槭ロ繝ｼ繧ｸ繝｣繝ｼ繝代せ繧堤｢ｺ隱・
                if (!string.IsNullOrEmpty(gmodCacheAddonsPath))
                {
                    gmaPath = Path.Combine(gmodCacheAddonsPath, $"{addonId}.gma");
                    if (!File.Exists(gmaPath))
                        gmaPath = null;
                }

                // 谺｡縺ｫ繧ｭ繝｣繝・す繝･繝代せ繧堤｢ｺ隱・
                if (gmaPath == null && !string.IsNullOrEmpty(gmodCachePath))
                {
                    gmaPath = Path.Combine(gmodCachePath, $"{addonId}.gma");
                    if (!File.Exists(gmaPath))
                        gmaPath = null;
                }

                if (gmaPath != null && File.Exists(gmaPath))
                {
                    // 繧ｿ繧､繝医Ν縺ｮ縺ｿ繧帝ｫ倬溘↓隱ｭ縺ｿ蜿悶ｋ
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

            // 邨先棡繧帝←逕ｨ縺励※繝励Ο繧ｰ繝ｬ繧ｹ繧呈峩譁ｰ
            foreach (var (addonId, title) in results)
            {
                if (!string.IsNullOrEmpty(title))
                {
                    var addon = configuration.AddonMetadata[addonId];
                    addon.Title = title;
                    addon.NeedsTitleUpdate = false; // 繧ｿ繧､繝医Ν譖ｴ譁ｰ螳御ｺ・ヵ繝ｩ繧ｰ繧偵け繝ｪ繧｢
                }

                processed++;
                var currentAddon = configuration.AddonMetadata[addonId];
                progress?.Report((processed, total, $"Processing: {currentAddon.Title}"));
            }

            // 險ｭ螳壹ｒ菫晏ｭ・
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

        private bool DirectoryHasAddonPayload(string directoryPath, string operationName)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            {
                return false;
            }

            try
            {
                return Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)
                    .Any(filePath => !IsIgnoredAddonPresenceMarker(filePath));
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning(
                    $"Failed to inspect addon directory payload at {directoryPath}. Treating directory as present. {ex.Message}",
                    operationName);
                return true;
            }
        }

        private static bool IsIgnoredAddonPresenceMarker(string filePath)
        {
            var fileName = Path.GetFileName(filePath);
            return string.Equals(fileName, ".gam_disabled", StringComparison.OrdinalIgnoreCase);
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
                    errorHandler.HandleWarning("Garry's Mod settings path is unknown; addonnomount.txt will not be updated.", "EnableAddon");
                }
                else
                {
                    var persisted = gmodAddonStateStore.SetEnabled(addonId, true);
                    if (!persisted)
                    {
                        errorHandler.HandleWarning($"Failed to persist addon state to addonnomount.txt for {addonId}.", "EnableAddon");
                    }
                }
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning($"Failed to update addonnomount.txt for {addonId}: {ex.Message}", "EnableAddon");
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

            // 譁ｰ譁ｹ蠑・ 騾壼ｸｸ縺ｮ繝・ぅ繝ｬ繧ｯ繝医Μ繧剃ｽ懈・縺励∽ｸｭ縺ｮGMA繝輔ぃ繧､繝ｫ縺縺代ワ繝ｼ繝峨Μ繝ｳ繧ｯ蛹・
            string sourceGmaPath = Path.Combine(sourcePath, $"{addonId}.gma");

	            if (File.Exists(sourceGmaPath))
	            {
	                // GMA繝輔ぃ繧､繝ｫ縺悟ｭ伜惠縺吶ｋ蝣ｴ蜷医∵眠譁ｹ蠑上ｒ菴ｿ逕ｨ
	                junctionService.CreateWorkshopAddonStructure(workshopPath, addonId, sourceGmaPath);
	            }
	            else
	            {
                // GMA繝輔ぃ繧､繝ｫ縺後↑縺・ｴ蜷医・蠕捺擂縺ｮ繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ譁ｹ蠑上ｒ菴ｿ逕ｨ
                if (Directory.Exists(workshopAddonPath))
                {
                    if (!junctionService.IsJunction(workshopAddonPath))
                    {
                        // 螳滉ｽ薙ヵ繧ｩ繝ｫ繝縺悟ｭ伜惠縺吶ｋ蝣ｴ蜷医√∪縺夂ｮ｡逅・ヵ繧ｩ繝ｫ繝縺ｫ遘ｻ蜍・
                        errorHandler.HandleWarning($"Found real folder instead of junction for addon {addonId}. Converting to managed addon.", "EnableAddon");

                        string tempPath = workshopAddonPath + "_temp_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                        ValidatePath(tempPath, "tempPath");
                        Directory.Move(workshopAddonPath, tempPath);

                        try
                        {
                            // 譌｢蟄倥・邂｡逅・ヵ繧ｩ繝ｫ繝縺ｨ繝槭・繧ｸ
                            MergeDirectories(tempPath, sourcePath);
                            Directory.Delete(tempPath, true);
                        }
                        catch
                        {
                            // 螟ｱ謨励＠縺溷ｴ蜷医・蜈・↓謌ｻ縺・
                            if (Directory.Exists(tempPath))
                            {
                                Directory.Move(tempPath, workshopAddonPath);
                            }
                            throw;
                        }
                    }
	                    else
	                    {
	                        // 繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ縺梧里縺ｫ蟄伜惠縺吶ｋ蝣ｴ蜷医ｂ縲√ち繝ｼ繧ｲ繝・ヨ謨ｴ蜷域ｧ縺ｮ縺溘ａCreateJunction縺ｫ蜃ｦ逅・ｒ蟋斐・繧・
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
                    errorHandler.HandleWarning("Garry's Mod settings path is unknown; addonnomount.txt will not be updated.", "EnableAddon");
                }
                else
                {
                    var persisted = gmodAddonStateStore.SetEnabled(addonId, true);
                    if (!persisted)
                    {
                        errorHandler.HandleWarning($"Failed to persist addon state to addonnomount.txt for {addonId}.", "EnableAddon");
                    }
                }
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning($"Failed to update addonnomount.txt for {addonId}: {ex.Message}", "EnableAddon");
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
            modeStrategy.DisableAddon(this, addonId);
        }

        internal void DisableAddonHard(string addonId)
        {
            try
            {
                if (gmodAddonStateStore == null)
                {
                    errorHandler.HandleWarning("Garry's Mod settings path is unknown; addonnomount.txt will not be updated.", "DisableAddon");
                }
                else
                {
                    var persisted = gmodAddonStateStore.SetEnabled(addonId, false);
                    if (!persisted)
                    {
                        errorHandler.HandleWarning($"Failed to persist addon state to addonnomount.txt for {addonId}.", "DisableAddon");
                    }
                }
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning($"Failed to update addonnomount.txt for {addonId}: {ex.Message}", "DisableAddon");
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
	                // 繧ｽ繝輔ヨ辟｡蜉ｹ蛹・ 繝輔ぃ繧､繝ｫ讒矩縺ｯ谿九＠縲∥ddons.txt縺ｨ繝｡繧ｿ繝・・繧ｿ縺ｮ縺ｿ譖ｴ譁ｰ
	                return;
	            }

	            // 繝上・繝臥┌蜉ｹ蛹・ 蜈医↓GMA縺ｮ邂｡逅・さ繝斐・繧堤｢ｺ菫昴＠縺ｦ縺九ｉ蜑企勁/遘ｻ蜍輔☆繧具ｼ域綾繧峨↑縺・撫鬘後・髦ｲ豁｢・・
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
                    errorHandler.HandleWarning("Garry's Mod settings path is unknown; addonnomount.txt will not be updated.", "DisableAddon");
                }
                else
                {
                    var persisted = gmodAddonStateStore.SetEnabled(addonId, false);
                    if (!persisted)
                    {
                        errorHandler.HandleWarning($"Failed to persist addon state to addonnomount.txt for {addonId}.", "DisableAddon");
                    }
                }
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning($"Failed to update addonnomount.txt for {addonId}: {ex.Message}", "DisableAddon");
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
                    errorHandler.HandleWarning("Garry's Mod settings path is unknown; addonnomount.txt will not be updated.", "EnableGmaAddon");
                }
                else
                {
                    var persisted = gmodAddonStateStore.SetEnabled(addonId, true);
                    if (!persisted)
                    {
                        errorHandler.HandleWarning($"Failed to persist addon state to addonnomount.txt for {addonId}.", "EnableGmaAddon");
                    }
                }
            }
            catch (Exception ex)
            {
                errorHandler.HandleWarning($"Failed to update addonnomount.txt for {addonId}: {ex.Message}", "EnableGmaAddon");
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

		            // Workshop縺ｮ逕溘ョ繝ｼ繧ｿ・井ｿ｡鬆ｼ縺ｧ縺阪ｋ繧ｽ繝ｼ繧ｹ・・
		            candidates.Add(Path.Combine(workshopPath, addonId, $"{addonId}.gma"));
		            candidates.Add(Path.Combine(workshopPath, addonId, $"{addonId}.cache"));

		            // 邂｡逅・ヵ繧ｩ繝ｫ繝・亥━蜈茨ｼ・
		            if (!string.IsNullOrEmpty(gmodCacheAddonsPath))
		            {
		                candidates.Add(Path.Combine(gmodCacheAddonsPath, addonId + ".gma"));
		            }

		            candidates.Add(Path.Combine(addonsPath, addonId, $"{addonId}.gma"));
		            candidates.Add(Path.Combine(addonsPath, $"{addonId}.gma"));

		            // GMod繧ｭ繝｣繝・す繝･・域怙蠕後・謇区ｮｵ・・
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
                var disabledIds = gmodAddonStateStore.GetDisabledIds();
                var softSnapshotStates = new Dictionary<string, bool>(StringComparer.Ordinal);

                foreach (var addonId in configuration.AddonMetadata.Keys
                             .Where(id => id != "*")
                             .OrderBy(id => id, StringComparer.Ordinal))
                {
                    softSnapshotStates[addonId] = !disabledIds.Contains(addonId);
                }

                return BuildSnapshot(softSnapshotStates, "actual:addonnomount.txt");
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
            var suffix = DisableMode == DisableMode.Soft ? "addonnomount.txt" : "actual";
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

                    // configuration縺系ull縺ｮ蝣ｴ蜷医・譁ｰ隕丈ｽ懈・
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
            var subscribeAsset = configuration.Assets.FirstOrDefault(a => a.IsSystem && (a.Name == "繧ｵ繝悶せ繧ｯ繝ｩ繧､繝・" || a.Name == "Subscribe"));
            if (subscribeAsset != null)
            {
                subscribeAsset.Name = "Subscribe";
                if (string.IsNullOrEmpty(subscribeAsset.Id))
                {
                    subscribeAsset.Id = "subscribe-system-asset";
                }
            }

            // Find and update Junction asset
            var junctionAsset = configuration.Assets.FirstOrDefault(a => a.IsSystem && (a.Name == "繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ" || a.Name == "Junction"));
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
                // CurrentVersion縺・1縺ｮ蝣ｴ蜷医・縺ｫ菫ｮ豁｣
                if (asset.CurrentVersion == -1)
                {
                    // [AddonManager] Fixing invalid CurrentVersion -1 for asset '{asset.Name}' to 0
                    asset.CurrentVersion = 0;
                }

                // 繧､繝ｳ繝昴・繝医・繝ｼ繧ｹ繝ｩ繧､繝ｳ縺後≠繧句ｴ蜷医〒繧ゅ，urrentVersion縺ｯ0莉･荳翫〒縺ゅｋ縺ｹ縺・
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
            await Task.CompletedTask; // 髱槫酔譛溘Γ繧ｽ繝・ラ繧堤ｶｭ謖・
        }

        /// <summary>
        /// 險ｭ螳壹ｒ蜊ｳ蠎ｧ縺ｫ菫晏ｭ假ｼ医ョ繝舌え繝ｳ繧ｹ繧堤┌隕厄ｼ・
        /// </summary>
        public async Task SaveConfigurationImmediatelyAsync()
        {
            errorHandler.HandleInfo($"SaveConfigurationImmediatelyAsync: Starting immediate save. Current assets count: {configuration.Assets.Count}", "SaveConfiguration");

            // 繝・ヰ繧ｦ繝ｳ繧ｹ繧ｿ繧､繝槭・繧偵く繝｣繝ｳ繧ｻ繝ｫ
            lock (_saveLock)
            {
                _saveDebounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _saveRequested = false;
            }

            // 蜊ｳ蠎ｧ縺ｫ菫晏ｭ・
            await SaveConfigurationInternalAsync();
            errorHandler.HandleInfo("SaveConfigurationImmediatelyAsync: Save completed", "SaveConfiguration");
        }

        /// <summary>
        /// 菫晏ｭ倥Μ繧ｯ繧ｨ繧ｹ繝医ｒ繧ｭ繝･繝ｼ縺励√ョ繝舌え繝ｳ繧ｹ縺吶ｋ
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
        /// 菫晉蕗荳ｭ縺ｮ菫晏ｭ倥ｒ螳溯｡・
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
        /// 螳滄圀縺ｮ菫晏ｭ伜・逅・
        /// </summary>
        private async Task SaveConfigurationInternalAsync()
        {
            const int maxRetries = 3;
            string json = null;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    // configuration縺ｮ繧ｹ繝翫ャ繝励す繝ｧ繝・ヨ繧剃ｽ懈・縺励※繧ｷ繝ｪ繧｢繝ｩ繧､繧ｺ
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

                        // 繧ｷ繝ｪ繧｢繝ｩ繧､繧ｺ
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
                // 繧｢繝医Α繝・け縺ｪ菫晏ｭ伜・逅・
                var tempPath = configPath + ".tmp";
                var backupPath = configPath + ".bak";

                // 1. 荳譎ゅヵ繧｡繧､繝ｫ縺ｫ譖ｸ縺崎ｾｼ縺ｿ
                await Task.Run(() => File.WriteAllText(tempPath, json));

                // 2. 迴ｾ蝨ｨ縺ｮ繝輔ぃ繧､繝ｫ縺悟ｭ伜惠縺吶ｋ蝣ｴ蜷医・繝舌ャ繧ｯ繧｢繝・・
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

                // 荳譎ゅヵ繧｡繧､繝ｫ縺ｮ蜑企勁繧定ｩｦ縺ｿ繧・
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
        /// 繝・ヰ繧ｦ繝ｳ繧ｹ譎る俣繧定ｨｭ螳・
        /// </summary>
        public int SaveDebounceMilliseconds
        {
            get => _saveDebounceMilliseconds;
            set
            {
                if (value < 100) value = 100; // 譛蟆・00ms
                if (value > 10000) value = 10000; // 譛螟ｧ10遘・
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

            // Undo險倬鹸
            undoManager.RecordAction(new UndoAction(UndoActionType.AssetCreated, $"Asset '{name}' created")
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

        public void AddAddonToAsset(string assetId, string addonId, AddonState state = AddonState.Enabled)
        {
            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId);
            if (asset != null && !asset.ContainsAllAddons())
            {
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
                    $"Added addon '{addonName}' to asset '{asset.Name}'")
                {
                    AssetId = assetId,
                    AssetName = asset.Name,
                    AddonId = addonId,
                    AddonName = addonName,
                    AddonState = state
                });

                // 繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ繧｢繧ｻ繝・ヨ縺ｫ霑ｽ蜉縺吶ｋ蝣ｴ蜷・
                if (assetId == "junction-system-asset")
                {
                    // 蜈・・繧｢繧ｻ繝・ヨ繧定ｨ倬鹸
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

                    // 莉悶・繧｢繧ｻ繝・ヨ縺九ｉ蜑企勁
                    foreach (var otherAsset in configuration.Assets)
                    {
                        if (otherAsset.Id != "junction-system-asset")
                        {
                            if (otherAsset.ContainsAllAddons())
                            {
                                // 蜈ｨ繧｢繝峨が繝ｳ繧貞性繧繧｢繧ｻ繝・ヨ縺ｮ蝣ｴ蜷医・縲・勁螟也憾諷九↓險ｭ螳・
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

        // 繝舌ャ繝∝・逅・畑繝｡繧ｽ繝・ラ - 螟ｧ驥上・繧｢繝峨が繝ｳ繧貞柑邇・噪縺ｫ霑ｽ蜉
        public void AddAddonsToAssetBatch(string assetId, List<string> addonIds, AddonState state = AddonState.Enabled)
        {
            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId);
            if (asset != null && !asset.ContainsAllAddons() && addonIds.Count > 0)
            {
                // Undo險倬鹸・医ヰ繝・メ蜈ｨ菴薙〒1縺､・・
                undoManager.RecordAction(new UndoAction(
                    UndoActionType.AddonAddedToAsset,
                    $"Added {addonIds.Count} addons to asset '{asset.Name}'")
                {
                    AssetId = assetId,
                    AssetName = asset.Name,
                    AddonId = string.Join(",", addonIds), // 隍・焚縺ｮID繧偵き繝ｳ繝槫玄蛻・ｊ縺ｧ菫晏ｭ・
                    AddonState = state
                });

                // 蜈ｨ縺ｦ縺ｮ繧｢繝峨が繝ｳ繧定ｿｽ蜉・育憾諷区峩譁ｰ縺ｪ縺暦ｼ・
                foreach (var addonId in addonIds)
                {
                    asset.AddAddon(addonId, state);
                }

                // 譛蠕後↓荳蠎ｦ縺縺醍憾諷九ｒ譖ｴ譁ｰ
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

                // 迴ｾ蝨ｨ縺ｮ迥ｶ諷九ｒ險倬鹸
                var currentState = asset.GetAddonState(addonId);

                // Undo險倬鹸
                var addonInfo = configuration.AddonMetadata.ContainsKey(addonId)
                    ? configuration.AddonMetadata[addonId]
                    : null;
                var addonName = addonInfo?.Title ?? addonId;

                undoManager.RecordAction(new UndoAction(
                    UndoActionType.AddonRemovedFromAsset,
                    $"Removed addon '{addonName}' from asset '{asset.Name}'")
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

            // Undo險倬鹸
            undoManager.RecordAction(new UndoAction(UndoActionType.AssetEnabled, $"Enabled asset '{asset.Name}'")
            {
                AssetId = assetId,
                AssetName = asset.Name,
                PreviousEnabledState = false
            });

            asset.Enabled = true;

            // 繧｢繧ｻ繝・ヨ蜀・・縺吶∋縺ｦ縺ｮ繧｢繝峨が繝ｳ繧呈怏蜉ｹ蛹・
            var addonIds = asset.ContainsAllAddons()
                ? configuration.AddonMetadata.Keys.ToList()
                : asset.Addons.ToList();

            foreach (var addonId in addonIds)
            {
                // 髯､螟悶＆繧後※縺・ｋ繧｢繝峨が繝ｳ縺ｯ繧ｹ繧ｭ繝・・
                if (asset.GetAddonState(addonId) != AddonState.Excluded)
                {
                    asset.SetAddonState(addonId, AddonState.Enabled);
                }
            }

            // 繧｢繝峨が繝ｳ迥ｶ諷九ｒ譖ｴ譁ｰ
            await UpdateAddonStatesAsync();
        }

        public async Task DisableAssetAsync(string assetId)
        {
            var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId);
            if (asset == null) return;

            // Undo險倬鹸
            undoManager.RecordAction(new UndoAction(UndoActionType.AssetDisabled, $"Disabled asset '{asset.Name}'")
            {
                AssetId = assetId,
                AssetName = asset.Name,
                PreviousEnabledState = true
            });

            asset.Enabled = false;

            // 繧｢繧ｻ繝・ヨ蜀・・縺吶∋縺ｦ縺ｮ繧｢繝峨が繝ｳ繧堤┌蜉ｹ蛹・
            var addonIds = asset.ContainsAllAddons()
                ? configuration.AddonMetadata.Keys.ToList()
                : asset.Addons.ToList();

            foreach (var addonId in addonIds)
            {
                // 迴ｾ蝨ｨ縺ｮ迥ｶ諷九′髯､螟悶〒縺ｪ縺・ｴ蜷医・縺ｿ辟｡蜉ｹ蛹・
                if (asset.GetAddonState(addonId) != AddonState.Excluded)
                {
                    asset.SetAddonState(addonId, AddonState.Disabled);
                }
            }

            // 繧｢繝峨が繝ｳ迥ｶ諷九ｒ譖ｴ譁ｰ
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
        /// 蜈ｨ繧｢繝峨が繝ｳ縺ｮ譛牙柑/辟｡蜉ｹ迥ｶ諷九ｒ譖ｴ譁ｰ
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
        /// 蜈ｨ繧｢繝峨が繝ｳ縺ｮ譛牙柑/辟｡蜉ｹ迥ｶ諷九ｒ譖ｴ譁ｰ・亥酔譛溽沿 - 蜀・Κ菴ｿ逕ｨ・・
        /// </summary>
        private void UpdateAddonStates()
        {
            var allAddonIds = configuration.AddonMetadata.Keys.ToList();

            foreach (var addonId in allAddonIds)
            {
                // "*" 縺ｯ迚ｹ谿翫↑蛟､縺ｪ縺ｮ縺ｧ繧ｹ繧ｭ繝・・
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
        /// 繧｢繝峨が繝ｳ縺ｮ譛邨ら噪縺ｪ譛牙柑/辟｡蜉ｹ迥ｶ諷九ｒ險育ｮ・
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
            // 髯､螟悶＆繧後※縺・ｋ縺九メ繧ｧ繝・け
            foreach (var asset in enabledAssets)
            {
                if (asset.ContainsAllAddons() || asset.Addons.Contains(addonId))
                {
                    var state = asset.GetAddonState(addonId);
                    if (state == AddonState.Excluded)
                    {
                        return false; // 髯､螟悶＆繧後※縺・ｋ蝣ｴ蜷医・蠢・★辟｡蜉ｹ
                    }
                }
            }

            // 繧ｵ繝悶せ繧ｯ繝ｩ繧､繝悶い繧ｻ繝・ヨ縺ｮ迥ｶ諷九ｒ繝√ぉ繝・け
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

            // 莉悶・繧｢繧ｻ繝・ヨ縺ｧ譛牙柑縺ｫ縺ｪ縺｣縺ｦ縺・ｋ縺九メ繧ｧ繝・け
            foreach (var asset in enabledAssets)
            {
                if (asset.IsSystem) continue;

                if (asset.ContainsAllAddons() || asset.Addons.Contains(addonId))
                {
                    var state = asset.GetAddonState(addonId);

                    if (state == AddonState.Enabled)
                    {
                        return true; // 譛牙柑迥ｶ諷九・繧｢繧ｻ繝・ヨ縺後≠繧後・譛牙柑
                    }
                    else if (state == AddonState.Disabled)
                    {
                        // 辟｡蜉ｹ縺ｮ蝣ｴ蜷医√し繝悶せ繧ｯ繝ｩ繧､繝悶い繧ｻ繝・ヨ縺梧怏蜉ｹ縺ｪ蝣ｴ蜷医・縺ｿ繧ｵ繝悶せ繧ｯ繝ｩ繧､繝悶↓萓晏ｭ・
                        if (isInSubscribe && isSubscribeEnabled && subscribeState != AddonState.Excluded)
                        {
                            return true;
                        }
                    }
                }
            }

            // 縺ｩ縺ｮ繧｢繧ｻ繝・ヨ縺ｫ繧ょ性縺ｾ繧後※縺・↑縺・√∪縺溘・蜈ｨ縺ｦ辟｡蜉ｹ縺ｮ蝣ｴ蜷・
            // 繧ｵ繝悶せ繧ｯ繝ｩ繧､繝悶い繧ｻ繝・ヨ縺ｮ迥ｶ諷九↓蠕薙≧
            if (subscribeAsset != null)
            {
                // 繧ｵ繝悶せ繧ｯ繝ｩ繧､繝悶い繧ｻ繝・ヨ縺梧怏蜉ｹ縺ｧ縲√°縺､繧｢繝峨が繝ｳ縺梧怏蜉ｹ迥ｶ諷九・蝣ｴ蜷医・縺ｿtrue
                return isSubscribeEnabled && isInSubscribe && subscribeState == AddonState.Enabled;
            }

            return false;
        }

        /// <summary>
        /// 繧｢繝峨が繝ｳ縺ｮ迥ｶ諷九ｒ險ｭ螳・
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

                // 迴ｾ蝨ｨ縺ｮ迥ｶ諷九ｒ險倬鹸
                var previousState = asset.GetAddonState(addonId);

                // Undo險倬鹸
                var addonInfo = configuration.AddonMetadata.ContainsKey(addonId)
                    ? configuration.AddonMetadata[addonId]
                    : null;
                var addonName = addonInfo?.Title ?? addonId;

                undoManager.RecordAction(new UndoAction(
                    UndoActionType.AddonStateChanged,
                    $"Changed addon '{addonName}' to {GetStateDisplayName(state)}")
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
                    // 蜊倅ｸ縺ｮ繧｢繝峨が繝ｳ縺ｮ迥ｶ諷九□縺代ｒ譖ｴ譁ｰ・郁ｻｽ驥丞喧・・
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
                case AddonState.Enabled: return "Enabled";
                case AddonState.Disabled: return "Disabled";
                case AddonState.Excluded: return "Excluded";
                default: return state.ToString();
            }
        }

        /// <summary>
        /// 蜊倅ｸ縺ｮ繧｢繝峨が繝ｳ縺ｮ迥ｶ諷九ｒ譖ｴ譁ｰ
        /// </summary>
        private void UpdateSingleAddonState(string addonId)
        {
            try
            {
                // "*" 縺ｯ迚ｹ谿翫↑蛟､縺ｪ縺ｮ縺ｧ繧ｹ繧ｭ繝・・・医ョ繝舌ャ繧ｰ繝ｬ繝吶Ν縺ｧ繝ｭ繧ｰ・・
                if (addonId == "*")
                {
                    // 繝ｯ繧､繝ｫ繝峨き繝ｼ繝峨・豁｣蟶ｸ縺ｪ蜍穂ｽ懊↑縺ｮ縺ｧ縲∬ｭｦ蜻翫〒縺ｯ縺ｪ縺上ョ繝舌ャ繧ｰ繝ｬ繝吶Ν縺ｧ繝ｭ繧ｰ
                    errorHandler.HandleInfo("Skipping wildcard addon ID '*' (this is normal behavior)", "UpdateSingleAddonState");
                    return;
                }

                // 繧｢繝峨が繝ｳID縺悟ｮ滄圀縺ｫ蟄伜惠縺吶ｋ縺狗｢ｺ隱・
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
        /// Undo繝槭ロ繝ｼ繧ｸ繝｣繝ｼ繧貞叙蠕・
        /// </summary>
        public UndoManager GetUndoManager() => undoManager;

        /// <summary>
        /// 譛蠕後・謫堺ｽ懊ｒ蜈・↓謌ｻ縺・
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
                        // 菴懈・縺輔ｌ縺溘い繧ｻ繝・ヨ繧貞炎髯､
                        if (action.AssetId != null)
                        {
                            configuration.Assets.RemoveAll(a => a.Id == action.AssetId);
                            await SaveConfigurationAsync();
                        }
                        break;

                    case UndoActionType.AssetDeleted:
                        // 蜑企勁縺輔ｌ縺溘い繧ｻ繝・ヨ繧貞ｾｩ蜈・
                        if (action.DeletedAsset != null)
                        {
                            configuration.Assets.Add(action.DeletedAsset);
                            await SaveConfigurationAsync();
                            UpdateAddonStates();
                        }
                        break;

                    case UndoActionType.AssetEnabled:
                    case UndoActionType.AssetDisabled:
                        // 繧｢繧ｻ繝・ヨ縺ｮ譛牙柑/辟｡蜉ｹ繧貞・縺ｫ謌ｻ縺・
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
                        // 繧｢繝峨が繝ｳ縺ｮ迥ｶ諷九ｒ蜈・↓謌ｻ縺・
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
                        // 繧｢繝峨が繝ｳ繧偵い繧ｻ繝・ヨ縺九ｉ蜑企勁
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
                        // 繧｢繝峨が繝ｳ繧偵い繧ｻ繝・ヨ縺ｫ霑ｽ蜉
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
        /// 繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ繧｢繧ｻ繝・ヨ縺悟ｭ伜惠縺吶ｋ縺薙→繧堤｢ｺ隱・
        /// </summary>
        private void EnsureJunctionAssetExists()
        {
            var junctionAsset = configuration.Assets.FirstOrDefault(a => a.Id == "junction-system-asset");
            if (junctionAsset == null)
            {
                junctionAsset = new Asset("繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ", true);
                junctionAsset.Id = "junction-system-asset";
                junctionAsset.Enabled = false;
                configuration.Assets.Add(junctionAsset);
            }
        }

        /// <summary>
        /// 繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ迥ｶ諷九・繧｢繝峨が繝ｳ繧呈､懷・縺励※繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ繧｢繧ｻ繝・ヨ繧呈峩譁ｰ
        /// </summary>
        public async Task UpdateJunctionAssetAsync()
        {
            try
            {
                var junctionAsset = configuration.Assets.FirstOrDefault(a => a.Id == "junction-system-asset");
                if (junctionAsset == null) return;

                // 蜈ｨ繧｢繝峨が繝ｳ繧偵メ繧ｧ繝・け
                var allAddons = configuration.AddonMetadata;
                var orphanedAddons = new List<string>();

                // 莉悶・繧｢繧ｻ繝・ヨ縺ｫ螻槭＠縺ｦ縺・ｋ繧｢繝峨が繝ｳ繧貞庶髮・
                var addonsInOtherAssets = new HashSet<string>();
                foreach (var asset in configuration.Assets)
                {
                    if (asset.Id != "junction-system-asset")
                    {
                        if (asset.ContainsAllAddons())
                        {
                            // 蜈ｨ繧｢繝峨が繝ｳ繧貞性繧繧｢繧ｻ繝・ヨ縺後≠繧句ｴ蜷医・縲∝・縺ｦ縺ｮ繧｢繝峨が繝ｳ縺檎ｮ｡逅・＆繧後※縺・ｋ
                            addonsInOtherAssets.UnionWith(allAddons.Keys);
                        }
                        else
                        {
                            addonsInOtherAssets.UnionWith(asset.Addons);
                        }
                    }
                }

                // 縺ｩ縺ｮ繧｢繧ｻ繝・ヨ縺ｫ繧ょｱ槭＠縺ｦ縺・↑縺・い繝峨が繝ｳ繧呈爾縺・
                foreach (var addon in allAddons)
                {
                    if (!addonsInOtherAssets.Contains(addon.Key))
                    {
                        var addonPath = Path.Combine(workshopPath, addon.Key);
                        var sourcePath = Path.Combine(addonsPath, addon.Key);

                        // 繧｢繝峨が繝ｳ縺悟ｮ滄圀縺ｫ蟄伜惠縺吶ｋ縺九メ繧ｧ繝・け
                        bool addonExists = false;

                        // Check if it's a GMA file addon
                        if (addon.Value.IsGmaFile)
                        {
                            string gmaCachePath = Path.Combine(gmodCachePath ?? "", addon.Key + ".gma");
                            string gmaSourcePath = Path.Combine(addonsPath, addon.Key + ".gma");

                            // GMA繝輔ぃ繧､繝ｫ縺檎ｮ｡逅・ヵ繧ｩ繝ｫ繝縺ｾ縺溘・繧ｭ繝｣繝・す繝･縺ｫ蟄伜惠縺吶ｋ縺狗｢ｺ隱・
                            addonExists = File.Exists(gmaSourcePath) || File.Exists(gmaCachePath);

                            // GMA file exists in managed folder = disabled
                            if (addonExists && File.Exists(gmaSourcePath) && !File.Exists(gmaCachePath))
                            {
                                orphanedAddons.Add(addon.Key);
                            }
                        }
                        else
                        {
                            // 繧ｽ繝ｼ繧ｹ繝・ぅ繝ｬ繧ｯ繝医Μ縺悟ｭ伜惠縺吶ｋ縺九メ繧ｧ繝・け
                            addonExists = Directory.Exists(sourcePath) ||
                                        (Directory.Exists(addonPath) && !junctionService.IsJunction(addonPath));

                            if (addonExists && Directory.Exists(sourcePath))
                            {
                                // 繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ縺ｮ蟄伜惠繧偵メ繧ｧ繝・け
                                bool hasJunction = Directory.Exists(addonPath) && junctionService.IsJunction(addonPath);

                                // 縺ｩ縺ｮ繧｢繧ｻ繝・ヨ縺ｫ繧ょｱ槭＠縺ｦ縺翫ｉ縺壹√ず繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ縺悟ｭ伜惠縺励↑縺・= 蟄､遶九＠縺溽┌蜉ｹ蛹悶い繝峨が繝ｳ
                                if (!hasJunction)
                                {
                                    orphanedAddons.Add(addon.Key);
                                }
                            }
                        }

                        // 繧｢繝峨が繝ｳ縺悟ｭ伜惠縺励↑縺・ｴ蜷医・繧ｹ繧ｭ繝・・・・orkshop縺九ｉ蜑企勁縺輔ｌ縺滂ｼ・
                        if (!addonExists)
                        {
                            continue;
                        }
                    }
                }

                // 繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ繧｢繧ｻ繝・ヨ繧呈峩譁ｰ・域・遉ｺ逧・↓霑ｽ蜉縺輔ｌ縺溘ｂ縺ｮ縺ｯ菫晄戟・・
                // 蟄､遶九＠縺溘い繝峨が繝ｳ縺ｮ縺ｿ繧定ｿｽ蜉
                foreach (var addonId in orphanedAddons)
                {
                    // "*" 縺ｯ迚ｹ谿翫↑蛟､縺ｪ縺ｮ縺ｧ霑ｽ蜉縺励↑縺・
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

                // 繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ繧｢繧ｻ繝・ヨ縺九ｉ蜑企勁縺吶∋縺阪い繝峨が繝ｳ繧堤音螳・
                // 縺溘□縺励∵焔蜍輔〒霑ｽ蜉縺輔ｌ縺溘い繝峨が繝ｳ縺ｯ菫晄戟縺吶ｋ
                var toRemove = new List<string>();
                foreach (var addonId in junctionAsset.Addons)
                {
                    // "*" 縺ｯ迚ｹ谿翫↑蛟､縺ｪ縺ｮ縺ｧ繧ｹ繧ｭ繝・・
                    if (addonId == "*")
                    {
                        errorHandler.HandleWarning("Removing wildcard '*' from junction asset", "UpdateJunctionAsset");
                        toRemove.Add(addonId);
                        continue;
                    }

                    // 莉悶・繧｢繧ｻ繝・ヨ縺ｫ蟄伜惠縺励√°縺､蟄､遶九＠縺ｦ縺・↑縺・い繝峨が繝ｳ縺ｮ縺ｿ蜑企勁蟇ｾ雎｡
                    // 縺溘□縺励、ddonStates縺ｫ險倬鹸縺輔ｌ縺ｦ縺・ｋ・域焔蜍戊ｿｽ蜉縺輔ｌ縺滂ｼ峨い繝峨が繝ｳ縺ｯ蜑企勁縺励↑縺・
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
        /// 繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ繧｢繝峨が繝ｳ繧貞・縺ｮ繧｢繧ｻ繝・ヨ縺ｫ謌ｻ縺・
        /// </summary>
        public void RestoreAddonFromJunction(string addonId)
        {
            // 繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ繧｢繧ｻ繝・ヨ縺九ｉ蜑企勁
            var junctionAsset = configuration.Assets.FirstOrDefault(a => a.Id == "junction-system-asset");
            if (junctionAsset != null)
            {
                junctionAsset.RemoveAddon(addonId);
            }

            // 蜈・・繧｢繧ｻ繝・ヨ縺ｫ謌ｻ縺・
            if (configuration.JunctionHistory.TryGetValue(addonId, out var sourceAssetIds))
            {
                foreach (var assetId in sourceAssetIds)
                {
                    var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId);
                    if (asset != null)
                    {
                        if (asset.ContainsAllAddons())
                        {
                            // 蜈ｨ繧｢繝峨が繝ｳ繧貞性繧繧｢繧ｻ繝・ヨ縺ｮ蝣ｴ蜷医・縲・勁螟也憾諷九ｒ隗｣髯､
                            asset.AddonStates.Remove(addonId);
                        }
                        else
                        {
                            asset.AddAddon(addonId, AddonState.Enabled);
                        }
                    }
                }

                // 螻･豁ｴ縺九ｉ蜑企勁
                configuration.JunctionHistory.Remove(addonId);
            }

            UpdateAddonStates();
        }

        /// <summary>
        /// 繧｢繝峨が繝ｳ縺ｮ蜈・・繧｢繧ｻ繝・ヨ繧貞叙蠕・
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
        /// 遘ｻ陦御ｸｭ縺ｫ譌｢蟄倥ョ繧｣繝ｬ繧ｯ繝医Μ縺瑚ｦ九▽縺九▲縺溷ｴ蜷医・蜃ｦ逅・
        /// </summary>
        private void HandleExistingDirectoryDuringMigration(string directory, string targetPath, string dirName)
        {
            // 螳滉ｽ薙ヵ繧ｩ繝ｫ繝縺悟ｭ伜惠縺吶ｋ蝣ｴ蜷医∫ｮ｡逅・ヵ繧ｩ繝ｫ繝縺ｫ遘ｻ蜍輔＠縺ｦ縺九ｉ繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ菴懈・

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
                        // 繝ｭ繝ｼ繝ｫ繝舌ャ繧ｯ縺ｯ髮｣縺励＞縺ｮ縺ｧ縲√ユ繝ｳ繝昴Λ繝ｪ繧呈ｮ九＠縺ｦ隴ｦ蜻翫↓逡吶ａ繧・
                        errorHandler.HandleError(ex,
                            $"Failed to merge addon {dirName} contents into managed folder. Leaving temp folder: {tempPath}",
                            ErrorSeverity.Warning);
                    }
                }
            }
            catch
            {
                // 螟ｱ謨励＠縺溷ｴ蜷医・蜈・↓謌ｻ縺・
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
        /// 繝・ぅ繝ｬ繧ｯ繝医Μ繧偵・繝ｼ繧ｸ縺吶ｋ
        /// </summary>
        private void MergeDirectories(string source, string destination)
        {
            ValidatePath(source, "source");
            ValidatePath(destination, "destination");

            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                // .NET Standard 2.0蟇ｾ蠢懊・縺溘ａ謇句虚縺ｧ逶ｸ蟇ｾ繝代せ繧定ｨ育ｮ・
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

            // RestoreOriginalStateAsync繧剃ｽｿ逕ｨ縺励※縲√☆縺ｹ縺ｦ縺ｮ繧｢繝峨が繝ｳ繧貞・縺ｮ迥ｶ諷九↓謌ｻ縺・
            await RestoreOriginalStateAsync();

            // 險ｭ螳壹ｒ蜀榊・譛溷喧
            configuration = new Configuration();

            // 繝・ぅ繝ｬ繧ｯ繝医Μ繧貞・菴懈・縺励※蛻晄悄蛹・
            await InitializeAsync();

            errorHandler.HandleInfo("Full reset completed successfully", "ResetManager");
        }

        public async Task RestoreOriginalStateAsync()
        {
            errorHandler.HandleInfo("Starting RestoreOriginalStateAsync", "RestoreOriginalState");

            // 繧ｹ繝・ャ繝・: 縺吶∋縺ｦ縺ｮ繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ/繝上・繝峨Μ繝ｳ繧ｯ繧貞炎髯､
            await RemoveAllJunctionsAndHardLinksAsync();

            // 繧ｹ繝・ャ繝・: 邂｡逅・ョ繧｣繝ｬ繧ｯ繝医Μ縺九ｉ繝輔ぃ繧､繝ｫ繧貞・縺ｮ蝣ｴ謇縺ｫ謌ｻ縺・
            await RestoreManagedAddonsAsync();

            // 繧ｹ繝・ャ繝・: 邂｡逅・ョ繧｣繝ｬ繧ｯ繝医Μ縺ｨ繧ｭ繝｣繝・す繝･繝・ぅ繝ｬ繧ｯ繝医Μ繧貞炎髯､
            await CleanupManagerDirectoriesAsync();

            // 繧ｹ繝・ャ繝・: 險ｭ螳壹ヵ繧｡繧､繝ｫ繧貞炎髯､・亥ｮ悟・縺ｫ繧ｯ繝ｪ繝ｼ繝ｳ縺ｪ迥ｶ諷九↓・・
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

            // Workshop繝代せ蜀・・縺吶∋縺ｦ縺ｮ繧ｨ繝ｳ繝医Μ繧偵メ繧ｧ繝・け
            foreach (var entry in Directory.GetDirectories(workshopPath))
            {
                var dirName = Path.GetFileName(entry);

                try
                {
                    // 繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ縺ｮ蝣ｴ蜷医・蜑企勁
                    if (junctionService.IsJunction(entry))
                    {
                        errorHandler.HandleInfo($"Removing junction: {dirName}", "RemoveAllJunctionsAndHardLinks");
                        junctionService.RemoveJunction(entry);
                    }
                    // 騾壼ｸｸ縺ｮ繝・ぅ繝ｬ繧ｯ繝医Μ縺ｧGMA繝上・繝峨Μ繝ｳ繧ｯ繧貞性繧蜿ｯ閭ｽ諤ｧ縺後≠繧句ｴ蜷・
                    else
                    {
                        var gmaPath = Path.Combine(entry, $"{dirName}.gma");
                        if (File.Exists(gmaPath))
                        {
                            // 繝上・繝峨Μ繝ｳ繧ｯ縺九←縺・°繝√ぉ繝・け縺励※蜑企勁
                            var managedGmaPath = Path.Combine(addonsPath, dirName, $"{dirName}.gma");
                            if (File.Exists(managedGmaPath) && junctionService.IsHardLink(gmaPath, managedGmaPath))
                            {
                                errorHandler.HandleInfo($"Removing hard link: {gmaPath}", "RemoveAllJunctionsAndHardLinks");
                                junctionService.RemoveHardLink(gmaPath);
                                // 遨ｺ縺ｮ繝・ぅ繝ｬ繧ｯ繝医Μ繧ょ炎髯､
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

            // 繧ｭ繝｣繝・す繝･繝・ぅ繝ｬ繧ｯ繝医Μ蜀・・繝上・繝峨Μ繝ｳ繧ｯ繧ょ炎髯､
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

            // 邂｡逅・ョ繧｣繝ｬ繧ｯ繝医Μ蜀・・縺吶∋縺ｦ縺ｮ繧｢繝峨が繝ｳ繧貞・縺ｮ蝣ｴ謇縺ｫ謌ｻ縺・
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
                        // 蜈・・蝣ｴ謇縺ｫ繝・ぅ繝ｬ繧ｯ繝医Μ縺悟ｭ伜惠縺励↑縺・ｴ蜷医・縺ｿ遘ｻ蜍・
                        if (!Directory.Exists(originalPath))
                        {
                            errorHandler.HandleInfo($"Moving addon {addonId} back to workshop", "RestoreManagedAddons");
                            Directory.Move(managedAddonPath, originalPath);
                        }
                        else
                        {
                            // 譌｢縺ｫ蟄伜惠縺吶ｋ蝣ｴ蜷医・繝槭・繧ｸ蜃ｦ逅・
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

            // 繧ｭ繝｣繝・す繝･邂｡逅・ョ繧｣繝ｬ繧ｯ繝医Μ蜀・・GMA繝輔ぃ繧､繝ｫ繧ょ・縺ｮ蝣ｴ謇縺ｫ謌ｻ縺・
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

            // 邂｡逅・ョ繧｣繝ｬ繧ｯ繝医Μ繧貞炎髯､
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

            // 繧ｭ繝｣繝・す繝･邂｡逅・ョ繧｣繝ｬ繧ｯ繝医Μ繧貞炎髯､
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
                    // 繧ｨ繝ｩ繝ｼ縺ｯ辟｡隕悶＠縺ｦ邯夊｡・
                }
            }
        }

        /// <summary>
        /// 繝ｪ繧ｽ繝ｼ繧ｹ縺ｮ繧ｯ繝ｪ繝ｼ繝ｳ繧｢繝・・
        /// </summary>
        /// <summary>
        /// 譛ｪ螳御ｺ・・謫堺ｽ懊ｒ繝√ぉ繝・け縺励※蠕ｩ譌ｧ繧ｪ繝励す繝ｧ繝ｳ繧呈署萓・
        /// </summary>
        private async Task CheckIncompleteOperationsAsync()
        {
            var incompleteLogs = operationLogManager.GetIncompleteLogs();
            if (incompleteLogs.Count > 0)
            {
                errorHandler.HandleWarning($"Found {incompleteLogs.Count} incomplete operations from previous session", "CheckIncompleteOperations");

                // 縺薙％縺ｧUI縺ｫ騾夂衍縺吶ｋ縺九∬・蜍募ｾｩ譌ｧ繧定ｩｦ縺ｿ繧・
                // 迴ｾ譎らせ縺ｧ縺ｯ隴ｦ蜻翫・縺ｿ
                foreach (var log in incompleteLogs)
                {
                    errorHandler.HandleWarning(
                        $"Incomplete {log.Type} operation from {log.StartTime:yyyy-MM-dd HH:mm:ss} with {log.Items.Count} items",
                        "IncompleteOperation"
                    );

                    // 謫堺ｽ懊ｒ繧ｯ繝ｪ繝ｼ繝ｳ繧｢繝・・・亥ｿ・ｦ√↓蠢懊§縺ｦ蠕ｩ譌ｧ蜃ｦ逅・ｒ霑ｽ蜉・・
                    operationLogManager.RemoveLog(log.Id);
                }
            }
        }

        /// <summary>
        /// 繧ｷ繧ｹ繝・Β縺ｮ謨ｴ蜷域ｧ繧偵メ繧ｧ繝・け縺励※菫ｮ蠕ｩ
        /// </summary>
	        private async Task ValidateSystemIntegrityAsync()
	        {
	            errorHandler.HandleInfo("Starting system integrity check...", "ValidateSystemIntegrity");

            var repairCount = 0;

            // 1. 險ｭ螳壹ヵ繧｡繧､繝ｫ縺ｮ繝舌ャ繧ｯ繧｢繝・・繝√ぉ繝・け
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

	            // 2. 繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ/繝上・繝峨Μ繝ｳ繧ｯ縺ｮ讀懆ｨｼ
	            foreach (var kvp in configuration.AddonMetadata.ToList())
	            {
	                var addon = kvp.Value;
	                var addonId = kvp.Key;

	                try
	                {
	                    if (addon.IsGmaFile)
	                    {
	                        // GMA繝輔ぃ繧､繝ｫ縺ｮ繝上・繝峨Μ繝ｳ繧ｯ繝√ぉ繝・け
	                        if (!string.IsNullOrEmpty(gmodCachePath))
	                        {
	                            var cachePath = Path.Combine(gmodCachePath, addonId + ".gma");

	                            // 譛牙柑蛹悶＆繧後ｋ縺ｹ縺阪い繝峨が繝ｳ縺ｮ縺ｿ縲√く繝｣繝・す繝･蛛ｴ縺ｮ繝ｪ繝ｳ繧ｯ谺謳阪ｒ菫ｮ蠕ｩ縺吶ｋ
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
	                        // 繝輔か繝ｫ繝繧｢繝峨が繝ｳ縺ｮ繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ繝√ぉ繝・け・・orkshop蛛ｴ縺ｫ繝ｪ繝ｳ繧ｯ縺後≠繧九・縺梧ｭ｣・・
	                        var workshopAddonPath = Path.Combine(workshopPath, addonId);
	                        var managedAddonPath = Path.Combine(addonsPath, addonId);

	                        // 譛溷ｾ・＆繧後ｋ譛邨ら憾諷九↓蝓ｺ縺･縺・※菫ｮ蠕ｩ・郁ｵｷ蜍慕峩蠕後↓UpdateAddonStatesAsync繧りｵｰ繧九◆繧√√％縺薙・螳牙・蛛ｴ縺ｫ蟇・○繧具ｼ・
	                        var shouldBeEnabled = CalculateFinalAddonState(addonId);

	                        if (shouldBeEnabled)
	                        {
	                            // 邂｡逅・ヵ繧ｩ繝ｫ繝螳滉ｽ薙′縺ゅｋ蝣ｴ蜷医・縺ｿ縲仝orkshop蛛ｴ縺ｮ谺謳阪ｒ陬懊≧
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
	                                    // 譌｢縺ｫ繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ縺ｪ繧峨，reateJunction縺ｮ蜷御ｸ繧ｿ繝ｼ繧ｲ繝・ヨ蛻､螳壹↓莉ｻ縺帙※謨ｴ蜷域ｧ繧貞叙繧・
	                                    CreateJunctionWithMetrics(workshopAddonPath, managedAddonPath);
	                                }
	                                else
	                                {
	                                    // 縺ｾ縺哦AM縺ｮ繧ｹ繧ｿ繝・.gam_disabled)縺ｪ繧牙ｮ牙・縺ｫ蟾ｮ縺玲崛縺亥庄閭ｽ
	                                    if (RemoveDisabledStub(workshopPath, addonId))
	                                    {
	                                        errorHandler.HandleWarning($"Repairing workshop stub for {addonId}", "ValidateSystemIntegrity");
	                                        CreateJunctionWithMetrics(workshopAddonPath, managedAddonPath);
	                                        repairCount++;
	                                    }
	                                    else
	                                    {
	                                        // 遨ｺ繝・ぅ繝ｬ繧ｯ繝医Μ・育ｧｻ陦悟､ｱ謨励↑縺ｩ縺ｧ谿九ｋ縺薙→縺後≠繧具ｼ峨↑繧牙ｮ牙・縺ｫ蟾ｮ縺玲崛縺医ｋ
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
	                                            // 螳滉ｽ薙ヵ繧ｩ繝ｫ繝・・team縺檎函謌・蜀好L遲会ｼ峨・蝣ｴ蜷医・閾ｪ蜍輔〒遘ｻ蜍輔・蜑企勁縺帙★隴ｦ蜻翫・縺ｿ
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
	                            // 辟｡蜉ｹ縺ｪ縺ｮ縺ｫWorkshop蛛ｴ縺ｫ繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ縺梧ｮ九▲縺ｦ縺・ｋ蝣ｴ蜷医・蜑企勁・亥ｮ滉ｽ薙ヵ繧ｩ繝ｫ繝縺ｯ隗ｦ繧峨↑縺・ｼ・
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

            // 譛ｪ菫晏ｭ倥ョ繝ｼ繧ｿ繧貞叉蠎ｧ縺ｫ菫晏ｭ・
            _saveDebounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            if (_saveRequested)
            {
                SaveConfigurationInternalAsync().GetAwaiter().GetResult();
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
                    // 繝輔ぃ繧､繝ｫ繝上Φ繝峨Ν繧貞叙蠕・
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

                    // 蜷後§繝懊Μ繝･繝ｼ繝縲∝酔縺炉ileIndex縺ｪ繧牙酔荳繝輔ぃ繧､繝ｫ・医ワ繝ｼ繝峨Μ繝ｳ繧ｯ・・
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
        /// 譌｢蟄倥・繝｡繧ｿ繝・・繧ｿ繧剃ｿｮ蠕ｩ縺吶ｋ
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

                // 1. FolderPath縺後く繝｣繝・す繝･繝・ぅ繝ｬ繧ｯ繝医Μ縺ｮGMA繝輔ぃ繧､繝ｫ繧呈欠縺励※縺・ｋ蝣ｴ蜷・
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

                // 2. FolderPath縺碁壼ｸｸ縺ｮ繝・ぅ繝ｬ繧ｯ繝医Μ繧呈欠縺励※縺・ｋ縺後∝ｮ滄圀縺ｫ縺ｯ繧ｭ繝｣繝・す繝･縺ｫGMA繝輔ぃ繧､繝ｫ縺悟ｭ伜惠縺吶ｋ蝣ｴ蜷・
                if (!addon.IsGmaFile && !string.IsNullOrEmpty(addon.FolderPath) && !addon.FolderPath.EndsWith(".gma"))
                {
                    // 繧ｭ繝｣繝・す繝･蜀・・GMA繝輔ぃ繧､繝ｫ繧偵メ繧ｧ繝・け
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
                        // GMA繝輔ぃ繧､繝ｫ縺悟ｭ伜惠縺吶ｋ蝣ｴ蜷医√Γ繧ｿ繝・・繧ｿ繧剃ｿｮ豁｣
                        addon.FolderPath = gmaPath;
                        addon.IsGmaFile = true;
                        needsUpdate = true;
                        errorHandler.HandleInfo($"Fixed path and IsGmaFile flag for {addonId}", "RepairAddonMetadata");
                    }
                }

                // 3. IsGmaFile縺荊rue縺縺後：olderPath縺梧ｭ｣縺励￥縺ｪ縺・ｴ蜷・
                if (addon.IsGmaFile)
                {
                    // 豁｣縺励＞GMA繝輔ぃ繧､繝ｫ繝代せ繧呈爾縺・
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

                // 4. 騾壼ｸｸ縺ｮ繧｢繝峨が繝ｳ縺ｮFolderPath繝√ぉ繝・け
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
        /// 驥崎､・い繝峨が繝ｳ繧偵け繝ｪ繝ｼ繝ｳ繧｢繝・・縺吶ｋ・亥酔縺露D縺ｧ繝・ぅ繝ｬ繧ｯ繝医Μ縺ｨGMA縺ｮ荳｡譁ｹ縺悟ｭ伜惠縺吶ｋ蝣ｴ蜷茨ｼ・
        /// </summary>
        public async Task CleanupDuplicateAddonsAsync()
        {
            errorHandler.HandleInfo("Starting duplicate addon cleanup...", "CleanupDuplicateAddons");

            var duplicatesFound = new List<string>();
            var cleanupOperations = new List<(string addonId, string action)>();

            // 1. 蜷後§繧｢繝峨が繝ｳID縺ｧ隍・焚縺ｮ蠖｢蠑上′蟄伜惠縺吶ｋ繧ｱ繝ｼ繧ｹ繧呈､懷・
            var addonGroups = configuration.AddonMetadata
                .GroupBy(kvp => kvp.Key)
                .Where(g => g.Count() > 1)
                .ToList();

            // 螳滄圀縺ｫ縺ｯ縲…onfiguration.AddonMetadata縺ｯ霎樊嶌縺ｪ縺ｮ縺ｧ驥崎､・く繝ｼ縺ｯ縺ｪ縺・′縲・
            // 蜷後§繧｢繝峨が繝ｳID縺ｧ繝・ぅ繝ｬ繧ｯ繝医Μ縺ｨGMA縺ｮ荳｡譁ｹ縺悟ｭ伜惠縺吶ｋ蝣ｴ蜷医ｒ讀懷・縺吶ｋ
            foreach (var kvp in configuration.AddonMetadata.ToList())
            {
                var addonId = kvp.Key;
                var addon = kvp.Value;

                // 繝・ぅ繝ｬ繧ｯ繝医Μ蠖｢蠑上・繧｢繝峨が繝ｳ縺ｮ蝣ｴ蜷・
                if (!addon.IsGmaFile)
                {
                    // 蜷後§ID縺ｮGMA繝輔ぃ繧､繝ｫ縺悟ｭ伜惠縺吶ｋ縺九メ繧ｧ繝・け
                    string? gmaPath = null;

                    // 繧ｭ繝｣繝・す繝･繝輔か繝ｫ繝蜀・・GMA繝輔ぃ繧､繝ｫ繧偵メ繧ｧ繝・け
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

                    // GMA繝輔ぃ繧､繝ｫ縺悟ｭ伜惠縺吶ｋ蝣ｴ蜷医・驥崎､・→縺励※讀懷・
                    if (gmaPath != null)
                    {
                        duplicatesFound.Add(addonId);

                        // 2. GMA蠖｢蠑上ｒ蜆ｪ蜈医＠縲√ョ繧｣繝ｬ繧ｯ繝医Μ蠖｢蠑上ｒ繝舌ャ繧ｯ繧｢繝・・縺ｾ縺溘・蜑企勁
                        string directoryPath = addon.FolderPath;
                        if (!string.IsNullOrEmpty(directoryPath) && Directory.Exists(directoryPath))
                        {
                            try
                            {
                                // 繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ繧貞炎髯､・域怏蜉ｹ蛹悶＆繧後※縺・ｋ蝣ｴ蜷茨ｼ・
                                if (addon.IsEnabled)
                                {
                                    string junctionPath = Path.Combine(workshopPath, addonId);
                                    if (Directory.Exists(junctionPath) && junctionService.IsJunction(junctionPath))
                                    {
                                        junctionService.RemoveJunction(junctionPath);
                                    }
                                }

                                // 繝・ぅ繝ｬ繧ｯ繝医Μ繧貞炎髯､
                                Directory.Delete(directoryPath, true);
                                cleanupOperations.Add((addonId, $"Deleted duplicate directory"));

                                errorHandler.HandleInfo($"Deleted duplicate directory addon {addonId}", "CleanupDuplicateAddons");

                                // 3. 繝｡繧ｿ繝・・繧ｿ繧呈峩譁ｰ・・MA蠖｢蠑上↓蛻・ｊ譖ｿ縺茨ｼ・
                                addon.IsGmaFile = true;
                                addon.FolderPath = gmaPath;

                                // GMA繝輔ぃ繧､繝ｫ縺九ｉ譛譁ｰ縺ｮ繝｡繧ｿ繝・・繧ｿ繧定ｪｭ縺ｿ霎ｼ繧
                                ReadGmaMetadata(gmaPath, addon);

                                // GMA蠖｢蠑上・繧｢繝峨が繝ｳ繧呈怏蜉ｹ蛹厄ｼ亥・縲・怏蜉ｹ縺縺｣縺溷ｴ蜷茨ｼ・
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

            // 險ｭ螳壹ｒ菫晏ｭ・
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
        /// 譌｢蟄倥・繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ縺九ｉ譁ｰ縺励＞繝上・繝峨Μ繝ｳ繧ｯ譁ｹ蠑上∈遘ｻ陦・
        /// </summary>
        public async Task MigrateToHardLinkSystemAsync()
        {
            errorHandler.HandleInfo("Starting migration from junction to hard link system...", "MigrateToHardLinkSystem");

            int migratedCount = 0;
            int failedCount = 0;
            var failedAddons = new List<string>();

            // 蜈ｨ縺ｦ縺ｮ繧｢繝峨が繝ｳ繧偵メ繧ｧ繝・け
            foreach (var kvp in configuration.AddonMetadata.ToList())
            {
                var addonId = kvp.Key;
                var addon = kvp.Value;

                // GMA繝輔ぃ繧､繝ｫ繧｢繝峨が繝ｳ縺ｯ繧ｹ繧ｭ繝・・
                if (addon.IsGmaFile)
                {
                    continue;
                }

                string workshopAddonPath = Path.Combine(workshopPath, addonId);
                string sourcePath = Path.Combine(addonsPath, addonId);
                string sourceGmaPath = Path.Combine(sourcePath, $"{addonId}.gma");

                // 繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ縺悟ｭ伜惠縺励√°縺､GMA繝輔ぃ繧､繝ｫ縺檎ｮ｡逅・ヵ繧ｩ繝ｫ繝縺ｫ蟄伜惠縺吶ｋ蝣ｴ蜷・
                if (Directory.Exists(workshopAddonPath) &&
                    junctionService.IsJunction(workshopAddonPath) &&
                    File.Exists(sourceGmaPath))
                {
                    try
                    {
                        // 1. 譌｢蟄倥・繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ繧貞炎髯､
                        junctionService.RemoveJunction(workshopAddonPath);

                        // 2. 譁ｰ譁ｹ蠑上〒繧｢繝峨が繝ｳ繧呈怏蜉ｹ蛹・
                        junctionService.CreateWorkshopAddonStructure(workshopPath, addonId, sourceGmaPath);

                        migratedCount++;
                        errorHandler.HandleInfo($"Migrated addon {addonId} to hard link system", "MigrateToHardLinkSystem");
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        failedAddons.Add(addonId);
                        errorHandler.HandleError(ex, $"Failed to migrate addon {addonId}", ErrorSeverity.Warning);

                        // 螟ｱ謨励＠縺溷ｴ蜷医・繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ繧貞ｾｩ蜈・
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

            // 邨先棡縺ｮ繧ｵ繝槭Μ繝ｼ繧定｡ｨ遉ｺ
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
        /// Workshop縺九ｉ蜑企勁縺輔ｌ縺溘い繝峨が繝ｳ繧偵け繝ｪ繝ｼ繝ｳ繧｢繝・・
        /// </summary>
        private async Task<HashSet<string>> CleanupDeletedWorkshopAddonsAsync(List<WorkshopAddon> currentAddons)
        {
            var deletedAddonIds = new HashSet<string>();
            try
            {
                var existingAddonIds = new HashSet<string>(currentAddons.Select(a => a.Id));
                var subscribedAddonIds = new HashSet<string>(StringComparer.Ordinal);
                var toRemove = new List<string>();

                try
                {
                    foreach (var addonId in GetSubscribedAddonIdsFromCache())
                    {
                        subscribedAddonIds.Add(addonId);
                    }
                }
                catch (Exception ex)
                {
                    errorHandler.HandleWarning($"Failed to read Steam Workshop cache during cleanup: {ex.Message}", "CleanupDeletedWorkshopAddons");
                }

                // 繝｡繧ｿ繝・・繧ｿ縺ｫ蟄伜惠縺吶ｋ縺後∝ｮ滄圀縺ｮ繝輔ぃ繧､繝ｫ縺悟ｭ伜惠縺励↑縺・い繝峨が繝ｳ繧呈､懷・
                foreach (var kvp in configuration.AddonMetadata)
                {
                    var addonId = kvp.Key;
                    var addon = kvp.Value;

                    bool fileExists = false;

                    if (addon.IsGmaFile)
                    {
                        // GMA繝輔ぃ繧､繝ｫ縺ｮ蟄伜惠遒ｺ隱・
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
                        // 繝・ぅ繝ｬ繧ｯ繝医Μ縺ｮ蟄伜惠遒ｺ隱・
                        string managedDirPath = Path.Combine(addonsPath, addonId);
                        string workshopDirPath = Path.Combine(workshopPath, addonId);

                        // 邂｡逅・ヵ繧ｩ繝ｫ繝縺悟ｭ伜惠縺吶ｋ蝣ｴ蜷医∫ｩｺ縺ｧ縺ｪ縺・°繝√ぉ繝・け
                        if (Directory.Exists(managedDirPath))
                        {
                            var hasFiles = Directory.GetFiles(managedDirPath, "*", SearchOption.AllDirectories).Any();
                            if (!hasFiles)
                            {
                                // 遨ｺ繝輔か繝ｫ繝縺ｯ蜑企勁
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

                // 蜑企勁縺輔ｌ縺溘い繝峨が繝ｳ繧偵け繝ｪ繝ｼ繝ｳ繧｢繝・・
                foreach (var addonId in toRemove)
                {
                    // 繝｡繧ｿ繝・・繧ｿ縺九ｉ蜑企勁
                    configuration.AddonMetadata.Remove(addonId);

                    // 蜈ｨ縺ｦ縺ｮ繧｢繧ｻ繝・ヨ縺九ｉ蜑企勁・・ubscribe繧｢繧ｻ繝・ヨ繧貞性繧・・
                    foreach (var asset in configuration.Assets)
                    {
                        if (asset.ContainsAllAddons() || asset.Addons.Contains(addonId))
                        {
                            asset.RemoveAddon(addonId);
                            // 繧｢繧ｻ繝・ヨ縺ｮ迥ｶ諷九°繧峨ｂ蜑企勁
                            if (asset.AddonStates.ContainsKey(addonId))
                            {
                                asset.AddonStates.Remove(addonId);
                            }
                        }
                    }

                    // 繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ螻･豁ｴ縺九ｉ蜑企勁
                    configuration.JunctionHistory.Remove(addonId);

                // Workshop驟堺ｸ九・讒矩縺ｯ螟画峩縺励↑縺・ｼ・team縺ｮ讀懆ｨｼ繝ｻ蜀好L隱倡匱繧帝∩縺代ｋ・・

                    // 邂｡逅・ヵ繧ｩ繝ｫ繝縺九ｉ繧｢繝峨が繝ｳ繝輔ぃ繧､繝ｫ繧貞炎髯､
                    try
                    {
                        // 繝・ぅ繝ｬ繧ｯ繝医Μ繧ｿ繧､繝励・繧｢繝峨が繝ｳ
                        string managedDirPath = Path.Combine(addonsPath, addonId);
                        if (Directory.Exists(managedDirPath))
                        {
                            Directory.Delete(managedDirPath, true);
                            errorHandler.HandleInfo($"Deleted managed directory: {managedDirPath}", "CleanupDeletedWorkshopAddons");
                        }

                        // GMA繝輔ぃ繧､繝ｫ繧ｿ繧､繝励・繧｢繝峨が繝ｳ
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

                    // 繧ｵ繝繝阪う繝ｫ繧ｭ繝｣繝・す繝･繧貞炎髯､
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

                    // 蜑企勁縺輔ｌ縺溘い繝峨が繝ｳID繧定ｿ斐☆
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
        /// Steam縺ｧ繧ｵ繝悶せ繧ｯ繝ｩ繧､繝冶ｧ｣髯､縺輔ｌ縺溘い繝峨が繝ｳ繧呈､懷・縺励※蜑企勁
        /// </summary>
        private async Task CleanupUnsubscribedAddonsAsync()
        {
            var toDelete = new List<string>();

            errorHandler.HandleInfo("Checking for unsubscribed addons...", "CleanupUnsubscribedAddons");

            foreach (var kvp in configuration.AddonMetadata)
            {
                var addonId = kvp.Key;
                var addon = kvp.Value;
                var workshopAddonPath = Path.Combine(this.workshopPath, addonId);

                // 繝ｯ繝ｼ繧ｯ繧ｷ繝ｧ繝・・繝輔か繝ｫ繝蛛ｴ縺悟ｮ悟・縺ｫ蟄伜惠縺励↑縺・ｼ医ず繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ繧ょｮ滉ｽ薙ｂGMA繝輔ぃ繧､繝ｫ繧ら┌縺・ｼ・
                bool workshopExists = addon.IsGmaFile
                    ? File.Exists(workshopAddonPath) ||
                      File.Exists(workshopAddonPath + ".gma") ||
                      DirectoryHasAddonPayload(workshopAddonPath, "CleanupUnsubscribedAddons")
                    : DirectoryHasAddonPayload(workshopAddonPath, "CleanupUnsubscribedAddons");

                if (!workshopExists)
                {
                    // 邂｡逅・ヵ繧ｩ繝ｫ繝蛛ｴ縺ｫ螳滉ｽ薙′蟄伜惠縺吶ｋ縺九メ繧ｧ繝・け
                    bool managedExists = false;

                    if (addon.IsGmaFile)
                    {
                        // GMA繝輔ぃ繧､繝ｫ縺ｮ蝣ｴ蜷・
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
                        // 繝・ぅ繝ｬ繧ｯ繝医Μ繧ｿ繧､繝励・蝣ｴ蜷・
                        string managedDirPath = Path.Combine(addonsPath, addonId);
                        managedExists = Directory.Exists(managedDirPath);
                    }

                    if (managedExists)
                    {
                        // 繝ｯ繝ｼ繧ｯ繧ｷ繝ｧ繝・・蛛ｴ縺ｫ辟｡縺・′邂｡逅・ヵ繧ｩ繝ｫ繝縺ｫ蟄伜惠 = 繧ｵ繝悶せ繧ｯ繝ｩ繧､繝冶ｧ｣髯､縺輔ｌ縺・
                        toDelete.Add(addonId);
                        errorHandler.HandleInfo($"Detected unsubscribed addon: {addonId}", "CleanupUnsubscribedAddons");
                    }
                }
            }

            // 讀懷・縺輔ｌ縺溘い繝峨が繝ｳ繧貞炎髯､
            foreach (var addonId in toDelete)
            {
                try
                {
                    // 邂｡逅・ヵ繧ｩ繝ｫ繝縺九ｉ蜑企勁
                    if (configuration.AddonMetadata[addonId].IsGmaFile)
                    {
                        // GMA繝輔ぃ繧､繝ｫ繧貞炎髯､
                        string managedGmaPath = Path.Combine(addonsPath, $"{addonId}.gma");
                        if (File.Exists(managedGmaPath))
                        {
                            File.Delete(managedGmaPath);
                            errorHandler.HandleInfo($"Deleted managed GMA file: {managedGmaPath}", "CleanupUnsubscribedAddons");
                        }

                        // 繧ｭ繝｣繝・す繝･縺九ｉ繧ょ炎髯､
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
                        // 繝・ぅ繝ｬ繧ｯ繝医Μ繧貞炎髯､
                        string managedDirPath = Path.Combine(addonsPath, addonId);
                        if (Directory.Exists(managedDirPath))
                        {
                            Directory.Delete(managedDirPath, true);
                            errorHandler.HandleInfo($"Deleted managed directory: {managedDirPath}", "CleanupUnsubscribedAddons");
                        }
                    }

                    // 繝｡繧ｿ繝・・繧ｿ縺九ｉ蜑企勁
                    configuration.AddonMetadata.Remove(addonId);

                    // 蜈ｨ縺ｦ縺ｮ繧｢繧ｻ繝・ヨ縺九ｉ蜑企勁
                    foreach (var asset in configuration.Assets)
                    {
                        if (asset.ContainsAllAddons() || asset.Addons.Contains(addonId))
                        {
                            asset.RemoveAddon(addonId);
                        }
                    }

                    // 繧ｸ繝｣繝ｳ繧ｯ繧ｷ繝ｧ繝ｳ螻･豁ｴ縺九ｉ蜑企勁
                    configuration.JunctionHistory.Remove(addonId);

                    // 繧ｵ繝繝阪う繝ｫ繧ｭ繝｣繝・す繝･繧貞炎髯､
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
        /// 繧ｭ繝｣繝・す繝･邂｡逅・・謨ｴ蜷域ｧ繝√ぉ繝・け縺ｨ菫ｮ蠕ｩ
        /// </summary>
        public async Task RepairCacheManagementAsync()
        {
            if (string.IsNullOrEmpty(gmodCachePath))
                return;

            errorHandler.HandleInfo("Starting cache management repair...", "RepairCacheManagement");

            // 邂｡逅・ヵ繧ｩ繝ｫ繝縺悟ｭ伜惠縺励↑縺・ｴ蜷医・菴懈・
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

            // 邂｡逅・＆繧後※縺・↑縺ЖMA繝輔ぃ繧､繝ｫ繧呈､懷・縺励※遘ｻ陦・
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

                        // 邂｡逅・ヵ繧ｩ繝ｫ繝縺ｫ遘ｻ蜍・
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

                        // 譛牙柑迥ｶ諷九ｒ邯ｭ謖√☆繧九◆繧√ワ繝ｼ繝峨Μ繝ｳ繧ｯ繧剃ｽ懈・
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
        /// Workshop縺九ｉ繧｢繝峨が繝ｳ繧偵し繝悶せ繧ｯ繝ｩ繧､繝悶☆繧・
        /// </summary>
        private async Task SubscribeToWorkshopAsync(string addonId)
        {
            try
            {
                // Steam URL繧ｹ繧ｭ繝ｼ繝繧剃ｽｿ逕ｨ縺励※繧ｵ繝悶せ繧ｯ繝ｩ繧､繝・
                var url = $"steam://subscribe/4000/{addonId}";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });

                // 蟆代＠蠕・ｩ滂ｼ・team蜃ｦ逅・・縺溘ａ・・
                await Task.Delay(100);

                errorHandler.HandleInfo($"Subscribed to workshop addon: {addonId}", "WorkshopSubscribe");
            }
            catch (Exception ex)
            {
                errorHandler.HandleError(ex, $"Failed to subscribe to workshop addon {addonId}", ErrorSeverity.Warning);
            }
        }

        /// <summary>
        /// Workshop縺九ｉ繧｢繝峨が繝ｳ縺ｮ繧ｵ繝悶せ繧ｯ繝ｩ繧､繝悶ｒ隗｣髯､縺吶ｋ
        /// </summary>
        private async Task UnsubscribeFromWorkshopAsync(string addonId)
        {
            try
            {
                // Steam URL繧ｹ繧ｭ繝ｼ繝繧剃ｽｿ逕ｨ縺励※繧ｵ繝悶せ繧ｯ繝ｩ繧､繝冶ｧ｣髯､
                var url = $"steam://unsubscribe/4000/{addonId}";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });

                // 蟆代＠蠕・ｩ滂ｼ・team蜃ｦ逅・・縺溘ａ・・
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
