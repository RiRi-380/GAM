using System.Collections.Generic;

namespace GmodAddonManager.Core.Services
{
    public interface ISteamPathDetector
    {
        string DetectWorkshopPath();
        PathSnapshot DetectPathSnapshot();
        bool IsGmodInstalled(string? workshopPath);
        string? DetectGmodCachePath();
        string? DetectSteamPath();
        List<string> GetSteamLibraryPaths(string? steamPath);
    }
}
