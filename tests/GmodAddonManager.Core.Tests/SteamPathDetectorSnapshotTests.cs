using GmodAddonManager.Core.Services;
using System.Diagnostics;

namespace GmodAddonManager.Core.Tests;

public sealed class SteamPathDetectorSnapshotTests
{
    [Fact]
    public void DetectPathSnapshot_ResolvesGmodInstallFromAppManifestIndependentlyFromWorkshopRoot()
    {
        using var env = new SteamLayout();
        var gmodLibrary = env.CreateLibrary("LibraryGmod");
        var workshopLibrary = env.CreateLibrary("LibraryWorkshop");
        env.WriteLibraryFolders(gmodLibrary, workshopLibrary);
        SteamLayout.WriteGmodInstall(gmodLibrary, "GarrysMod");
        SteamLayout.WriteWorkshopManifest(workshopLibrary);
        SteamLayout.WriteWorkshopPayload(workshopLibrary, "123456789");

        var detector = new SteamPathDetector(env.SteamPath);

        var snapshot = detector.DetectPathSnapshot();

        Assert.NotNull(snapshot.GmodInstall);
        Assert.Equal(
            Path.Combine(gmodLibrary, "steamapps", "common", "GarrysMod"),
            snapshot.GmodInstall!.InstallPath);
        Assert.Equal(PathCandidateConfidence.High, snapshot.GmodInstall.Confidence);
        Assert.NotNull(snapshot.ActiveWorkshopRoot);
        Assert.Equal(
            Path.Combine(workshopLibrary, "steamapps", "workshop", "content", "4000"),
            snapshot.ActiveWorkshopRoot!.RootPath);
        Assert.Equal(PathCandidateConfidence.High, snapshot.ActiveWorkshopRoot.Confidence);
    }

    [Fact]
    public void DetectPathSnapshot_PrefersWorkshopRootWithValidPayloadOverEmptyRoot()
    {
        using var env = new SteamLayout();
        var emptyLibrary = env.CreateLibrary("LibraryEmptyWorkshop");
        var validLibrary = env.CreateLibrary("LibraryValidWorkshop");
        env.WriteLibraryFolders(emptyLibrary, validLibrary);
        SteamLayout.WriteGmodInstall(validLibrary, "GarrysMod");
        SteamLayout.WriteWorkshopManifest(emptyLibrary);
        SteamLayout.CreateWorkshopFolder(emptyLibrary, "111111111");
        SteamLayout.WriteWorkshopManifest(validLibrary);
        SteamLayout.WriteWorkshopPayload(validLibrary, "222222222");

        var detector = new SteamPathDetector(env.SteamPath);

        var snapshot = detector.DetectPathSnapshot();

        Assert.NotNull(snapshot.ActiveWorkshopRoot);
        Assert.Equal(
            Path.Combine(validLibrary, "steamapps", "workshop", "content", "4000"),
            snapshot.ActiveWorkshopRoot!.RootPath);
        Assert.Equal(1, snapshot.ActiveWorkshopRoot.ValidPayloadCount);
    }

    [Fact]
    public void WorkshopInstallIndex_IgnoresEmptyWorkshopFolders()
    {
        using var env = new SteamLayout();
        var library = env.CreateLibrary("LibraryWorkshop");
        env.WriteLibraryFolders(library);
        SteamLayout.CreateWorkshopFolder(library, "111111111");
        SteamLayout.WriteWorkshopPayload(library, "222222222");
        var detector = new SteamPathDetector(env.SteamPath);
        var index = new WorkshopInstallIndex(detector, TimeSpan.Zero);

        var ids = index.GetInstalledIds();

        Assert.DoesNotContain("111111111", ids);
        Assert.Contains("222222222", ids);
    }

    [Fact]
    public void DetectPathSnapshot_RejectsWorkshopRootWhoseJunctionTargetIsMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var env = new SteamLayout();
        var library = env.CreateLibrary("LibraryDanglingWorkshop");
        env.WriteLibraryFolders(library);
        SteamLayout.WriteGmodInstall(library, "GarrysMod");
        SteamLayout.WriteWorkshopManifest(library);

        var workshopRoot = Path.Combine(
            library,
            "steamapps",
            "workshop",
            "content",
            "4000");
        Directory.CreateDirectory(Path.GetDirectoryName(workshopRoot)!);
        var missingTarget = Path.Combine(library, "missing-workshop-target");

        try
        {
            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("mklink");
            startInfo.ArgumentList.Add("/J");
            startInfo.ArgumentList.Add(workshopRoot);
            startInfo.ArgumentList.Add(missingTarget);

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            process.WaitForExit();
            Assert.True(
                process.ExitCode == 0,
                $"Could not create test junction: {process.StandardError.ReadToEnd()}");

            var snapshot = new SteamPathDetector(env.SteamPath).DetectPathSnapshot();

            Assert.Null(snapshot.ActiveWorkshopRoot);
            var rejected = Assert.Single(
                snapshot.WorkshopRoots,
                candidate => string.Equals(
                    candidate.RootPath,
                    workshopRoot,
                    StringComparison.OrdinalIgnoreCase));
            Assert.Equal(PathCandidateConfidence.Rejected, rejected.Confidence);
            Assert.False(rejected.ContentRootExists);
            Assert.Contains(
                rejected.RejectReasons,
                reason => reason.Contains("Failed to inspect", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(workshopRoot))
            {
                Directory.Delete(workshopRoot);
            }
        }
    }

    private sealed class SteamLayout : IDisposable
    {
        private readonly string rootPath;

        public SteamLayout()
        {
            rootPath = Path.Combine(Path.GetTempPath(), "gam-steam-layout-tests-" + Guid.NewGuid().ToString("N"));
            SteamPath = Path.Combine(rootPath, "Steam");
            Directory.CreateDirectory(Path.Combine(SteamPath, "steamapps"));
            File.WriteAllText(Path.Combine(SteamPath, "steam.exe"), string.Empty);
        }

        public string SteamPath { get; }

        public string CreateLibrary(string name)
        {
            var libraryPath = Path.Combine(rootPath, name);
            Directory.CreateDirectory(Path.Combine(libraryPath, "steamapps"));
            return libraryPath;
        }

        public void WriteLibraryFolders(params string[] libraryPaths)
        {
            var builder = new System.Text.StringBuilder();
            builder.AppendLine("\"libraryfolders\"");
            builder.AppendLine("{");
            for (var i = 0; i < libraryPaths.Length; i++)
            {
                builder.Append("    \"").Append(i).AppendLine("\"");
                builder.AppendLine("    {");
                builder.Append("        \"path\" \"")
                    .Append(libraryPaths[i].Replace(@"\", @"\\", StringComparison.Ordinal))
                    .AppendLine("\"");
                builder.AppendLine("    }");
            }
            builder.AppendLine("}");

            File.WriteAllText(Path.Combine(SteamPath, "steamapps", "libraryfolders.vdf"), builder.ToString());
        }

        public static void WriteGmodInstall(string libraryPath, string installDir)
        {
            var steamAppsPath = Path.Combine(libraryPath, "steamapps");
            Directory.CreateDirectory(steamAppsPath);
            File.WriteAllText(
                Path.Combine(steamAppsPath, "appmanifest_4000.acf"),
                string.Join(Environment.NewLine, new[]
                {
                    "\"AppState\"",
                    "{",
                    "    \"appid\" \"4000\"",
                    $"    \"installdir\" \"{installDir}\"",
                    "}"
                }));
            Directory.CreateDirectory(Path.Combine(libraryPath, "steamapps", "common", installDir, "garrysmod", "cfg"));
        }

        public static void WriteWorkshopManifest(string libraryPath)
        {
            var workshopPath = Path.Combine(libraryPath, "steamapps", "workshop");
            Directory.CreateDirectory(workshopPath);
            File.WriteAllText(Path.Combine(workshopPath, "appworkshop_4000.acf"), "\"AppWorkshop\"{}");
        }

        public static void CreateWorkshopFolder(string libraryPath, string addonId)
        {
            Directory.CreateDirectory(Path.Combine(libraryPath, "steamapps", "workshop", "content", "4000", addonId));
        }

        public static void WriteWorkshopPayload(string libraryPath, string addonId)
        {
            var addonPath = Path.Combine(libraryPath, "steamapps", "workshop", "content", "4000", addonId);
            Directory.CreateDirectory(addonPath);
            var luaPath = Path.Combine(addonPath, "lua");
            Directory.CreateDirectory(luaPath);
            File.WriteAllText(Path.Combine(luaPath, "autorun.lua"), "print('ok')");
        }

        public void Dispose()
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, true);
            }
        }
    }
}
