using System;
using System.Collections.Generic;

namespace GmodAddonManager.Core.Services
{
    public sealed class AddonManagerOptions
    {
        public DisableMode DisableMode { get; set; } = DisableMode.Soft;
        public string? CustomWorkshopPath { get; set; }
        public string? CustomGmodInstallPath { get; set; }
        public string? CustomAppDataPath { get; set; }
        public string? CustomGmodCachePath { get; set; }
        public IReadOnlyList<string>? CustomWorkshopCacheFilePaths { get; set; }
        public bool DisableCacheScan { get; set; } = false;
        public IErrorHandler? ErrorHandler { get; set; }
        public TimeSpan ScanCacheTtl { get; set; } = TimeSpan.FromSeconds(15);
        public int? MaxParallelAddonStateUpdates { get; set; }
        public int? MaxParallelWorkshopScans { get; set; }
        /// <summary>
        /// Discovers local addons for read-only presentation. This never enables
        /// the legacy payload move/link management paths.
        /// </summary>
        public bool EnableLocalAddonDiscoveryExperimental { get; set; } = false;

        /// <summary>
        /// Legacy test/experiment switch for physical local-addon management.
        /// Production UI must not set this option.
        /// </summary>
        public bool EnableLocalAddonsExperimental { get; set; } = false;
    }
}
