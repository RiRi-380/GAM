using System.Collections.Generic;

namespace GmodAddonManager.Core.Models
{
    public sealed class WorkshopCollectionInfo
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string PreviewUrl { get; set; } = "";
        public List<string> AddonIds { get; set; } = new();
    }
}
