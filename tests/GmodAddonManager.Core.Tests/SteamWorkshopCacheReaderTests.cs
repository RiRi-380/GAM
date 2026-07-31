using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class SteamWorkshopCacheReaderTests
{
    [Fact]
    public void ParseWorkshopSnapshot_SeparatesSubscribedAndInstalledIds()
    {
        var observedAtUtc = new DateTime(2026, 7, 31, 12, 34, 56, DateTimeKind.Utc);
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
            "        \"222\"",
            "        {",
            "            \"title\" \"Installed And Subscribed\"",
            "            \"subscribedby\" \"76561198000000000\"",
            "        }",
            "        \"333\"",
            "        {",
            "            \"title\" \"Download Pending\"",
            "            \"subscribedby\" \"76561198000000000\"",
            "        }",
            "        \"444\"",
            "        {",
            "            \"title\" \"Cached Details Only\"",
            "        }",
            "    }",
            "}"
        });

        var snapshot = SteamWorkshopCacheReader.ParseWorkshopSnapshot(content, observedAtUtc);

        Assert.True(snapshot.IsAuthoritative);
        Assert.Equal(observedAtUtc, snapshot.ObservedAtUtc);
        Assert.Equal(DateTimeKind.Utc, snapshot.ObservedAtUtc.Kind);
        Assert.Equal(new[] { "222", "333" }, snapshot.SubscribedIds);
        Assert.Equal(new[] { "111", "222" }, snapshot.InstalledIds);
    }

    [Fact]
    public void ParseWorkshopSnapshot_WithoutSubscribedBy_FallsBackToAllDetailChildren()
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
            "        }",
            "    }",
            "    \"WorkshopItemDetails\"",
            "    {",
            "        \"111\"",
            "        {",
            "            \"title\" \"First\"",
            "        }",
            "        \"222\"",
            "        {",
            "            \"title\" \"Download Pending\"",
            "        }",
            "    }",
            "}"
        });

        var snapshot = SteamWorkshopCacheReader.ParseWorkshopSnapshot(content, DateTime.UtcNow);

        Assert.True(snapshot.IsAuthoritative);
        Assert.Equal(new[] { "111", "222" }, snapshot.SubscribedIds);
        Assert.Equal(new[] { "111" }, snapshot.InstalledIds);
    }

    [Fact]
    public void ParseWorkshopSnapshot_SupportsLegacyFlatInstalledItems()
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
            "    \"WorkshopItemDetails\"",
            "    {",
            "        \"111\"",
            "        {",
            "            \"title\" \"First\"",
            "        }",
            "    }",
            "}"
        });

        var snapshot = SteamWorkshopCacheReader.ParseWorkshopSnapshot(content, DateTime.UtcNow);

        Assert.True(snapshot.IsAuthoritative);
        Assert.Equal(new[] { "111" }, snapshot.SubscribedIds);
        Assert.Equal(new[] { "111", "222" }, snapshot.InstalledIds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"AppWorkshop\" { \"WorkshopItemsInstalled\" { } }")]
    [InlineData("\"AppWorkshop\" { \"WorkshopItemDetails\" { } }")]
    [InlineData("\"AppWorkshop\" {")]
    [InlineData("\"AppWorkshop\" { \"WorkshopItemsInstalled\" { garbage } \"WorkshopItemDetails\" { } }")]
    [InlineData("\"AppWorkshop\" { \"WorkshopItemsInstalled\" { } \"WorkshopItemDetails\" { \"111\" \"not-a-section\" } }")]
    [InlineData("\"AppWorkshop\" { \"WorkshopItemsInstalled\" { \"not-an-id\" { } } \"WorkshopItemDetails\" { } }")]
    public void ParseWorkshopSnapshot_MissingOrMalformedContent_IsNotAuthoritative(string content)
    {
        var snapshot = SteamWorkshopCacheReader.ParseWorkshopSnapshot(content, DateTime.UtcNow);

        Assert.False(snapshot.IsAuthoritative);
    }

    [Fact]
    public void ParseWorkshopSnapshot_CommentsAndEmptySectionsRemainAuthoritative()
    {
        var content =
            "\uFEFF// Steam may include comments before the root\n" +
            "\"AppWorkshop\"\n" +
            "{\n" +
            "    \"WorkshopItemsInstalled\" { /* no installed items */ }\n" +
            "    \"WorkshopItemDetails\" { // no subscriptions\n }\n" +
            "}\n";

        var snapshot = SteamWorkshopCacheReader.ParseWorkshopSnapshot(
            content,
            DateTime.UtcNow);

        Assert.True(snapshot.IsAuthoritative);
        Assert.Empty(snapshot.SubscribedIds);
        Assert.Empty(snapshot.InstalledIds);
    }

    [Fact]
    public void GetWorkshopSnapshot_CombinesAllCacheFiles()
    {
        using var env = new CacheTestEnvironment();
        var cacheA = env.WriteSnapshotCache(
            "a.acf",
            subscribedIds: new[] { "111", "222" },
            installedIds: new[] { "111" });
        var cacheB = env.WriteSnapshotCache(
            "b.acf",
            subscribedIds: new[] { "222", "333" },
            installedIds: new[] { "222", "444" });

        var snapshot = SteamWorkshopCacheReader.GetWorkshopSnapshot(new[] { cacheA, cacheB });

        Assert.True(snapshot.IsAuthoritative);
        Assert.Equal(new[] { "111", "222", "333" }, snapshot.SubscribedIds);
        Assert.Equal(new[] { "111", "222", "444" }, snapshot.InstalledIds);
        Assert.Equal(DateTimeKind.Utc, snapshot.ObservedAtUtc.Kind);
    }

    [Fact]
    public void GetWorkshopSnapshot_MissingManifest_PreservesObservedDataButIsNotAuthoritative()
    {
        using var env = new CacheTestEnvironment();
        var cache = env.WriteSnapshotCache(
            "present.acf",
            subscribedIds: new[] { "111" },
            installedIds: new[] { "222" });
        var missing = Path.Combine(env.RootPath, "missing.acf");

        var snapshot = SteamWorkshopCacheReader.GetWorkshopSnapshot(new[] { cache, missing });

        Assert.False(snapshot.IsAuthoritative);
        Assert.Equal(new[] { "111" }, snapshot.SubscribedIds);
        Assert.Equal(new[] { "222" }, snapshot.InstalledIds);
    }

    [Fact]
    public void GetSubscribedAddonIds_CompatibilityReturnsSubscribedRatherThanInstalledIds()
    {
        using var env = new CacheTestEnvironment();
        var cache = env.WriteSnapshotCache(
            "compat.acf",
            subscribedIds: new[] { "333" },
            installedIds: new[] { "111", "222" });

        var ids = SteamWorkshopCacheReader.GetSubscribedAddonIds(new[] { cache });

        Assert.Equal(new[] { "333" }, ids);
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
    public void DiscoveredSnapshot_IgnoresUnrelatedLibraryWithoutGmodManifest()
    {
        using var env = new CacheTestEnvironment();
        var rootCache = env.WriteSteamCache(env.SteamPath, ("111", "Root"));
        var unrelatedLibrary = env.CreateLibrary("unrelated-library");
        env.WriteLibraryFolders(unrelatedLibrary);

        var discoveredPaths =
            SteamWorkshopCacheReader.GetWorkshopCacheFilePaths(env.SteamPath);
        var snapshot =
            SteamWorkshopCacheReader.GetWorkshopSnapshot(discoveredPaths);

        Assert.Equal([rootCache], discoveredPaths);
        Assert.True(snapshot.IsAuthoritative);
        Assert.Equal(["111"], snapshot.SubscribedIds);
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
        public string RootPath => rootPath;

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

        public string WriteSnapshotCache(
            string fileName,
            IReadOnlyCollection<string> subscribedIds,
            IReadOnlyCollection<string> installedIds)
        {
            var path = Path.IsPathRooted(fileName)
                ? fileName
                : Path.Combine(rootPath, fileName);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, BuildSnapshotCache(subscribedIds, installedIds));
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

        private static string BuildSnapshotCache(
            IEnumerable<string> subscribedIds,
            IEnumerable<string> installedIds)
        {
            var builder = new System.Text.StringBuilder();
            builder.AppendLine("\"AppWorkshop\"");
            builder.AppendLine("{");
            builder.AppendLine("    \"WorkshopItemsInstalled\"");
            builder.AppendLine("    {");

            foreach (var addonId in installedIds)
            {
                builder.Append("        \"").Append(addonId).AppendLine("\"");
                builder.AppendLine("        {");
                builder.AppendLine("            \"size\" \"4096\"");
                builder.AppendLine("        }");
            }

            builder.AppendLine("    }");
            builder.AppendLine("    \"WorkshopItemDetails\"");
            builder.AppendLine("    {");

            foreach (var addonId in subscribedIds)
            {
                builder.Append("        \"").Append(addonId).AppendLine("\"");
                builder.AppendLine("        {");
                builder.Append("            \"title\" \"Addon ").Append(addonId).AppendLine("\"");
                builder.AppendLine("            \"subscribedby\" \"76561198000000000\"");
                builder.AppendLine("        }");
            }

            builder.AppendLine("    }");
            builder.AppendLine("}");
            return builder.ToString();
        }
    }
}
