using System;

namespace GmodAddonManager.UI.Services
{
    public static class AddonTitleHelper
    {
        private const string LegacyPrefix = "Workshop-";

        public static string BuildPlaceholderTitle(string addonId)
        {
            var prefix = GetLocalizedPrefix();
            return $"{prefix}{addonId}";
        }

        public static bool IsPlaceholderTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return false;
            }

            var prefix = GetLocalizedPrefix();
            if (!string.IsNullOrEmpty(prefix) && title.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }

            return title.StartsWith(LegacyPrefix, StringComparison.Ordinal);
        }

        private static string GetLocalizedPrefix()
        {
            var prefix = L.Get("Addon.PlaceholderPrefix");
            if (string.IsNullOrWhiteSpace(prefix) ||
                string.Equals(prefix, "Addon.PlaceholderPrefix", StringComparison.Ordinal))
            {
                return LegacyPrefix;
            }

            return prefix;
        }
    }
}
