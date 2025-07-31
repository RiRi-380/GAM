using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GmodAddonManager.Core.Services
{
    public class SteamPathDetector : ISteamPathDetector
    {
        private const string GMOD_APP_ID = "4000";
        private const string WORKSHOP_RELATIVE_PATH = @"steamapps\workshop\content\" + GMOD_APP_ID;
        
        private readonly List<string> commonSteamPaths = new List<string>
        {
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
            @"D:\Steam",
            @"D:\SteamLibrary",
            @"E:\Steam",
            @"E:\SteamLibrary"
        };

        public string DetectWorkshopPath()
        {
            // DetectWorkshopPath: Starting
            
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new PlatformNotSupportedException("This application currently only supports Windows.");
            }

            string steamPath = DetectSteamPath();
            // DetectWorkshopPath: steamPath detected
            
            if (!string.IsNullOrEmpty(steamPath))
            {
                string workshopPath = Path.Combine(steamPath, WORKSHOP_RELATIVE_PATH);
                // DetectWorkshopPath: Checking workshop path
                
                if (Directory.Exists(workshopPath))
                {
                    // DetectWorkshopPath: Found workshop path
                    return workshopPath;
                }

                var libraryPaths = GetSteamLibraryPaths(steamPath);
                // DetectWorkshopPath: Checking library paths
                
                foreach (var libraryPath in libraryPaths)
                {
                    workshopPath = Path.Combine(libraryPath, WORKSHOP_RELATIVE_PATH);
                    // DetectWorkshopPath: Checking library path
                    
                    if (Directory.Exists(workshopPath))
                    {
                        // DetectWorkshopPath: Found at library path
                        return workshopPath;
                    }
                }
            }

            // DetectWorkshopPath: Failed to find workshop path
            throw new DirectoryNotFoundException("Could not find Garry's Mod workshop folder. Please ensure Steam and Garry's Mod are installed.");
        }

        public string DetectSteamPath()
        {
            string registryPath = TryGetSteamPathFromRegistry();
            if (!string.IsNullOrEmpty(registryPath) && Directory.Exists(registryPath))
            {
                return registryPath;
            }

            foreach (var path in commonSteamPaths)
            {
                if (Directory.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        private string TryGetSteamPathFromRegistry()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam"))
                {
                    if (key != null)
                    {
                        object steamPath = key.GetValue("SteamPath");
                        if (steamPath != null)
                        {
                            string path = steamPath.ToString().Replace('/', '\\');
                            return path;
                        }
                    }
                }

                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                {
                    if (key != null)
                    {
                        object installPath = key.GetValue("InstallPath");
                        if (installPath != null)
                        {
                            return installPath.ToString();
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            return null;
        }

        public List<string> GetSteamLibraryPaths(string steamPath)
        {
            var libraryPaths = new List<string> { steamPath };

            try
            {
                string libraryFoldersPath = Path.Combine(steamPath, @"steamapps\libraryfolders.vdf");
                if (File.Exists(libraryFoldersPath))
                {
                    string content = File.ReadAllText(libraryFoldersPath);
                    
                    var lines = content.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        if (line.Contains("\"path\""))
                        {
                            int startIndex = line.IndexOf("\"path\"") + 6;
                            string pathLine = line.Substring(startIndex).Trim();
                            
                            startIndex = pathLine.IndexOf('"') + 1;
                            int endIndex = pathLine.LastIndexOf('"');
                            
                            if (startIndex > 0 && endIndex > startIndex)
                            {
                                string libraryPath = pathLine.Substring(startIndex, endIndex - startIndex);
                                libraryPath = libraryPath.Replace("\\\\", "\\");
                                
                                if (Directory.Exists(libraryPath) && !libraryPaths.Contains(libraryPath))
                                {
                                    libraryPaths.Add(libraryPath);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            return libraryPaths;
        }

        public bool IsGmodInstalled(string workshopPath)
        {
            if (string.IsNullOrEmpty(workshopPath) || !Directory.Exists(workshopPath))
                return false;

            string gmodPath = Path.GetFullPath(Path.Combine(workshopPath, @"..\..\..\common\GarrysMod"));
            return Directory.Exists(gmodPath);
        }

        public string DetectGmodCachePath()
        {
            try
            {
                
                string workshopPath = DetectWorkshopPath();
                // DetectGmodCachePath: workshopPath detected
                
                if (string.IsNullOrEmpty(workshopPath))
                {
                    // DetectGmodCachePath: Returning null because workshopPath is empty
                    return null;
                }

                // From workshop path, navigate to Gmod cache folder
                // steamapps\workshop\content\4000 -> steamapps\common\GarrysMod\garrysmod\cache\workshop
                string gmodPath = Path.GetFullPath(Path.Combine(workshopPath, @"..\..\..\common\GarrysMod"));
                // DetectGmodCachePath: gmodPath detected
                
                if (!Directory.Exists(gmodPath))
                {
                    // DetectGmodCachePath: Returning null because gmodPath doesn't exist
                    return null;
                }

                string cachePath = Path.Combine(gmodPath, @"garrysmod\cache\workshop");
                // DetectGmodCachePath: cachePath detected
                
                // Create cache directory if it doesn't exist
                if (!Directory.Exists(cachePath))
                {
                    // DetectGmodCachePath: Creating cache directory
                    Directory.CreateDirectory(cachePath);
                }

                // DetectGmodCachePath: Returning cachePath
                return cachePath;
            }
            catch (Exception ex)
            {
                // DetectGmodCachePath error
                return null;
            }
        }
    }
}