namespace GmodAddonManager.Core.Models
{
    public class AssetApplyResult
    {
        public string AssetId { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string? BeforeHash { get; set; }
        public string? AfterHash { get; set; }
        public string? ExpectedHash { get; set; }
        public string? ErrorCode { get; set; }
        public long DurationMs { get; set; }
    }
}
