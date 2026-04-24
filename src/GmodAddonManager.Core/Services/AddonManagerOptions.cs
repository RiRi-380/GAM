namespace GmodAddonManager.Core.Services
{
    public sealed class AddonManagerOptions
    {
        public string? CustomWorkshopPath { get; set; }
        public string? CustomAppDataPath { get; set; }
        public IErrorHandler? ErrorHandler { get; set; }
        public DisableMode DisableMode { get; set; } = DisableMode.Soft;
        public bool DisableCacheScan { get; set; } = false;
    }
}
