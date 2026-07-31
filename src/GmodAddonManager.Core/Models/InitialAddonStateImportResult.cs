using System.Collections.Generic;

namespace GmodAddonManager.Core.Models
{
    public sealed class InitialAddonStateImportResult
    {
        public bool Completed { get; set; }

        public bool CreatedAsset { get; set; }

        public string? CreatedAssetId { get; set; }

        public IReadOnlyList<string> ImportedAddonIds { get; set; } = new List<string>();
    }
}
