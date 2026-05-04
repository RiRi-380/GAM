using System;

namespace GmodAddonManager.Core.Services
{
    internal static class ImageContentTypePolicy
    {
        public static bool AllowsImageDownload(string? mediaType)
        {
            return string.IsNullOrEmpty(mediaType) ||
                   mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase);
        }
    }
}
