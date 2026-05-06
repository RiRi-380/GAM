using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class SteamWorkshopCacheReaderTests
{
    [Fact]
    public void ParseSubscribedAddonIds_ReadsOnlyInstalledItems()
    {
        var content = string.Join(Environment.NewLine, new[]
        {
            "\"AppWorkshop\"",
            "{",
            "    \"WorkshopItemsInstalled\"",
            "    {",
            "        \"111\"",
            "        {",
            "            \"size\" \"4096\"",
            "            \"timeupdated\" \"1700000000\"",
            "        }",
            "        \"222\"",
            "        {",
            "            \"timeupdated\" \"1700000001\"",
            "        }",
            "    }",
            "    \"WorkshopItemDetails\"",
            "    {",
            "        \"333\"",
            "        {",
            "            \"title\" \"Details Only\"",
            "        }",
            "    }",
            "}"
        });

        var ids = SteamWorkshopCacheReader.ParseSubscribedAddonIds(content);

        Assert.Equal(new[] { "111", "222" }, ids);
    }

    [Fact]
    public void ParseSubscribedAddonIds_SupportsLegacyFlatInstalledItems()
    {
        var content = string.Join(Environment.NewLine, new[]
        {
            "\"AppWorkshop\"",
            "{",
            "    \"WorkshopItemsInstalled\"",
            "    {",
            "        \"111\" \"1\"",
            "        \"222\" \"1\"",
            "    }",
            "}"
        });

        var ids = SteamWorkshopCacheReader.ParseSubscribedAddonIds(content);

        Assert.Equal(new[] { "111", "222" }, ids);
    }

    [Fact]
    public void GetSubscribedAddonIds_CombinesAllCacheFiles()
    {
        using var env = new CacheTestEnvironment();
        var cacheA = env.WriteCacheFile("a.acf", ("111", "First"), ("222", "Second"));
        var cacheB = env.WriteCacheFile("b.acf", ("222", "Duplicate"), ("333", "Third"));

        var ids = SteamWorkshopCacheReader.GetSubscribedAddonIds(new[] { cacheA, cacheB });

        Assert.Equal(new[] { "111", "222", "333" }, ids);
    }

    [Fact]
    public void GetWorkshopCacheFilePaths_IncludesSteamLibrariesOnly()
    {
        using var env = new CacheTestEnvironment();
        var rootCache = env.WriteSteamCache(env.SteamPath, ("111", "Root"));
        var libraryPath = env.CreateLibrary("library-a");
        var libraryCache = env.WriteSteamCache(libraryPath, ("222", "Library"));
        env.WriteUserCache("123456789", ("333", "Userdata"));
        env.WriteLibraryFolders(libraryPath);

        var paths = SteamWorkshopCacheReader.GetWorkshopCacheFilePaths(env.SteamPath);

        Assert.Equal(
            new[] { rootCache, libraryCache }.OrderBy(path => path, StringComparer.OrdinalIgnoreCase),
            paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseAddonDetails_ReadsNestedItems()
    {
        var content = string.Join(Environment.NewLine, new[]
        {
            "\"AppWorkshop\"",
            "{",
            "    \"WorkshopItemDetails\"",
            "    {",
            "        \"111\"",
            "        {",
            "            \"title\" \"First Addon\"",
            "            \"tags\" \"fun\"",
            "            \"timeupdated\" \"1700000000\"",
            "        }",
            "        \"222\"",
            "        {",
            "            \"title\" \"Second Addon\"",
            "        }",
            "    }",
            "}"
        });

        var details = SteamWorkshopCacheReader.ParseAddonDetails(content);

        Assert.Equal("First Addon", details["111"].Title);
        Assert.Equal("fun", details["111"].Tags);
        Assert.True(details["111"].TimeUpdated.HasValue);
        Assert.Equal("Second Addon", details["222"].Title);
    }

    private sealed class CacheTestEnvironment : IDisposable
    {
        private readonly string rootPath;

        public CacheTestEnvironment()
        {
            rootPath = Path.Combine(Path.GetTempPath(), "gam-cache-tests-" + Guid.NewGuid().ToString("N"));
            SteamPath = Path.Combine(rootPath, "Steam");
            Directory.CreateDirectory(SteamPath);
        }

        public string SteamPath { get; }

        public string CreateLibrary(string name)
        {
            var libraryPath = Path.Combine(rootPath, name);
            Directory.CreateDirectory(libraryPath);
            return libraryPath;
        }

        public string WriteSteamCache(string libraryPath, params (string Id, string Title)[] addons)
        {
            return WriteCacheFile(Path.Combine(libraryPath, "steamapps", "workshop", "appworkshop_4000.acf"), addons);
        }

        public string WriteUserCache(string userId, params (string Id, string Title)[] addons)
        {
            return WriteCacheFile(Path.Combine(SteamPath, "userdata", userId, "ugc", "appworkshop_4000.acf"), addons);
        }

        public string WriteCacheFile(string fileName, params (string Id, string Title)[] addons)
        {
            var path = Path.IsPathRooted(fileName)
                ? fileName
                : Path.Combine(rootPath, fileName);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, BuildCache(addons));
            return path;
        }

        public void WriteLibraryFolders(params string[] libraryPaths)
        {
            var steamAppsPath = Path.Combine(SteamPath, "steamapps");
            Directory.CreateDirectory(steamAppsPath);

            var builder = new System.Text.StringBuilder();
            builder.AppendLine("\"libraryfolders\"");
            builder.AppendLine("{");

            for (var i = 0; i < libraryPaths.Length; i++)
            {
                builder.Append("    \"").Append(i + 1).AppendLine("\"");
                builder.AppendLine("    {");
                builder.Append("        \"path\" \"")
                    .Append(libraryPaths[i].Replace(@"\", @"\\", StringComparison.Ordinal))
                    .AppendLine("\"");
                builder.AppendLine("    }");
            }

            builder.AppendLine("}");

            File.WriteAllText(Path.Combine(steamAppsPath, "libraryfolders.vdf"), builder.ToString());
        }

        public void Dispose()
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, true);
            }
        }

        private static string BuildCache(params (string Id, string Title)[] addons)
        {
            var builder = new System.Text.StringBuilder();
            builder.AppendLine("\"AppWorkshop\"");
            builder.AppendLine("{");
            builder.AppendLine("    \"WorkshopItemsInstalled\"");
            builder.AppendLine("    {");

            foreach (var addon in addons)
            {
                builder.Append("        \"").Append(addon.Id).AppendLine("\"");
                builder.AppendLine("        {");
                builder.AppendLine("            \"size\" \"4096\"");
                builder.AppendLine("            \"timeupdated\" \"1700000000\"");
                builder.AppendLine("        }");
            }

            builder.AppendLine("    }");
            builder.AppendLine("    \"WorkshopItemDetails\"");
            builder.AppendLine("    {");

            foreach (var addon in addons)
            {
                builder.Append("        \"").Append(addon.Id).AppendLine("\"");
                builder.AppendLine("        {");
                builder.Append("            \"title\" \"").Append(addon.Title).AppendLine("\"");
                builder.AppendLine("        }");
            }

            builder.AppendLine("    }");
            builder.AppendLine("}");
            return builder.ToString();
        }
    }
}
