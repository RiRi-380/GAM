using System.Collections.Generic;

namespace GmodAddonManager.Core.Models
{
    public sealed class ConfigurationMigrationResult
    {
        public bool Changed { get; set; }

        public List<string> NeedsReviewAssetIds { get; } = new List<string>();

        public List<string> RemovedLegacySystemAssetIds { get; } = new List<string>();
    }
}
