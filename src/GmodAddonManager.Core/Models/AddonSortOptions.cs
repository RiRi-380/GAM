namespace GmodAddonManager.Core.Models
{
    public enum AddonSortMode
    {
        RecentlySubscribed,
        Name,
        Size,
        WorkshopUpdated
    }

    public enum AddonSortDirection
    {
        Ascending,
        Descending
    }

    public sealed class AddonSortOptions
    {
        public AddonSortMode Mode { get; set; } = AddonSortMode.RecentlySubscribed;

        public AddonSortDirection Direction { get; set; } = AddonSortDirection.Descending;
    }
}
