using System;
using System.Collections.Generic;
using System.IO;

namespace GmodAddonManager.Core.Services
{
    public sealed class WorkshopInstallIndex
    {
        private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromSeconds(30);
        private readonly ISteamPathDetector _steamPathDetector;
        private readonly object _lock = new object();
        private readonly TimeSpan _cacheTtl;
        private DateTime _cachedAtUtc = DateTime.MinValue;
        private string? _cachedSteamPath;
        private Dictionary<string, string>? _index;

        public WorkshopInstallIndex(ISteamPathDetector steamPathDetector, TimeSpan? cacheTtl = null)
        {
            _steamPathDetector = steamPathDetector ?? throw new ArgumentNullException(nameof(steamPathDetector));
            _cacheTtl = cacheTtl ?? DefaultCacheTtl;
        }

        public bool IsInstalled(string workshopId)
        {
            return TryGetInstallPath(workshopId, out _);
        }

        public bool TryGetInstallPath(string workshopId, out string? path)
        {
            path = null;
            if (string.IsNullOrWhiteSpace(workshopId))
            {
                return false;
            }

            var index = EnsureIndex();
            if (index.TryGetValue(workshopId, out var found))
            {
                path = found;
                return true;
            }

            return false;
        }

        public IReadOnlyCollection<string> GetInstalledIds()
        {
            var index = EnsureIndex();
            return new List<string>(index.Keys);
        }

        private Dictionary<string, string> EnsureIndex()
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var steamPath = _steamPathDetector.DetectSteamPath();
                if (_index != null &&
                    string.Equals(_cachedSteamPath, steamPath, StringComparison.OrdinalIgnoreCase) &&
                    now - _cachedAtUtc <= _cacheTtl)
                {
                    return _index;
                }

                _index = BuildIndex(steamPath);
                _cachedSteamPath = steamPath;
                _cachedAtUtc = now;
                return _index;
            }
        }

        private Dictionary<string, string> BuildIndex(string? steamPath)
        {
            var index = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(steamPath))
            {
                return index;
            }

            var libraryPaths = _steamPathDetector.GetSteamLibraryPaths(steamPath);
            foreach (var libraryPath in libraryPaths)
            {
                var root = Path.Combine(libraryPath, "steamapps", "workshop", "content", "4000");
                if (!Directory.Exists(root))
                {
                    continue;
                }

                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(root))
                    {
                        var name = Path.GetFileName(dir);
                        if (string.IsNullOrEmpty(name))
                        {
                            continue;
                        }

                        if (!index.ContainsKey(name))
                        {
                            index[name] = dir;
                        }
                    }
                }
                catch
                {
                    // Ignore per-library enumeration errors.
                }
            }

            return index;
        }
    }
}
