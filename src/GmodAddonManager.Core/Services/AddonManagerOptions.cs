using System;

namespace GmodAddonManager.Core.Services
{
    public sealed class AddonManagerOptions
    {
        public DisableMode DisableMode { get; set; } = DisableMode.Soft;
        public string? CustomWorkshopPath { get; set; }
        public IErrorHandler? ErrorHandler { get; set; }
        public TimeSpan ScanCacheTtl { get; set; } = TimeSpan.FromSeconds(15);
        public int? MaxParallelAddonStateUpdates { get; set; }
        public bool EnableLocalAddonsExperimental { get; set; } = false;
    }
}
