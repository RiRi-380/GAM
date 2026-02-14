using System;
using System.Text.RegularExpressions;

namespace GmodAddonManager.Core.Services
{
    public static class SteamUrlParser
    {
        private static readonly Regex WorkshopIdPattern = new Regex(
            @"(?:https?://)?(?:www\.)?steamcommunity\.com/sharedfiles/filedetails/?\?id=(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );

        private static readonly Regex ShortUrlPattern = new Regex(
            @"(?:https?://)?(?:www\.)?steamcommunity\.com/workshop/filedetails/?\?id=(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );

        public static string? ExtractWorkshopId(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            // Try standard URL pattern
            var match = WorkshopIdPattern.Match(url);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            // Try alternative URL pattern
            match = ShortUrlPattern.Match(url);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            // If the input is already a workshop ID (just numbers)
            if (Regex.IsMatch(url.Trim(), @"^\d+$"))
            {
                return url.Trim();
            }

            return null;
        }

        public static bool IsValidWorkshopUrl(string? url)
        {
            return !string.IsNullOrEmpty(ExtractWorkshopId(url));
        }

        public static string BuildWorkshopUrl(string workshopId)
        {
            if (string.IsNullOrWhiteSpace(workshopId))
            {
                throw new ArgumentException("Workshop ID cannot be null or empty", nameof(workshopId));
            }

            return $"https://steamcommunity.com/sharedfiles/filedetails/?id={workshopId}";
        }
    }
}
