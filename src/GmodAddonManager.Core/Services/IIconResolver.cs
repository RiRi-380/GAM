using System.Threading.Tasks;

namespace GmodAddonManager.Core.Services
{
    public interface IIconResolver
    {
        /// <summary>
        /// Resolves and returns the local path to an icon for the specified workshop item ID.
        /// Implements a multi-stage fallback system:
        /// 1. Check local icons cache
        /// 2. Check GMOD .cache files
        /// 3. Check Steam library cache
        /// 4. Download from Steam CDN
        /// </summary>
        /// <param name="workshopId">The workshop item ID</param>
        /// <returns>The local path to the PNG icon file, or null if not found</returns>
        Task<string?> GetIconAsync(ulong workshopId);

        /// <summary>
        /// Prewarm the icon cache for a list of workshop IDs
        /// </summary>
        /// <param name="workshopIds">List of workshop IDs to prewarm</param>
        Task PrewarmIconsAsync(ulong[] workshopIds);

        /// <summary>
        /// Clear all cached icons
        /// </summary>
        Task ClearCacheAsync();
    }
}