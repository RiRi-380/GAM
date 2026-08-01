namespace GmodAddonManager.Core.Models
{
    /// <summary>
    /// Stable persisted identities for GAM-owned Assets. System Asset identity is
    /// determined by ID, never by a localized or user-visible label.
    /// </summary>
    public static class SystemAssetDefinitions
    {
        public const string SubscribeId = "subscribe-system-asset";
        public const string SubscribeName = "Subscribe Asset";

        public const string GmodDisabledId = "gmod-disabled-system-asset";
        public const string GmodDisabledName = "GMod Disabled Addons";

        public const string JunctionId = "junction-system-asset";
        public const string JunctionName = "Junction";
    }
}
