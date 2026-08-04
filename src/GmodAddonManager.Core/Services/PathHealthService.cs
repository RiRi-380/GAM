using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GmodAddonManager.Core.Models;

namespace GmodAddonManager.Core.Services
{
    public sealed class PathHealthReport
    {
        public PathSnapshot CurrentSnapshot { get; set; } = new PathSnapshot();
        public PathSnapshot? PreviousSnapshot { get; set; }
        public PathSnapshot? LastKnownGoodSnapshot { get; set; }
        public IReadOnlyList<string> Issues { get; set; } = Array.Empty<string>();
        public IReadOnlyList<StaleMetadataRepairCandidate> MetadataRepairCandidates { get; set; } = Array.Empty<StaleMetadataRepairCandidate>();
        public AddonNoMountMigrationPlan AddonNoMountMigrationPlan { get; set; } = new AddonNoMountMigrationPlan();
        public IReadOnlyList<ManagedDataMigrationCandidate> ManagedDataMigrationCandidates { get; set; } = Array.Empty<ManagedDataMigrationCandidate>();

        public int IssueCount => Issues.Count;
        public int MetadataRepairCount => MetadataRepairCandidates.Count;
        public int AddonNoMountMigrationCount => AddonNoMountMigrationPlan.ToMigrateIds.Count;
        public int ManagedMigrationCandidateCount => ManagedDataMigrationCandidates.Count;
    }

    public sealed class StaleMetadataRepairCandidate
    {
        public string AddonId { get; set; } = string.Empty;
        public string OldPath { get; set; } = string.Empty;
        public string NewPath { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }

    public sealed class AddonNoMountMigrationPlan
    {
        public string? SourcePath { get; set; }
        public string? TargetPath { get; set; }
        public IReadOnlyList<string> SourceIds { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> TargetIds { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> ToMigrateIds { get; set; } = Array.Empty<string>();
        public bool HasWork => !string.IsNullOrWhiteSpace(SourcePath) &&
                               !string.IsNullOrWhiteSpace(TargetPath) &&
                               ToMigrateIds.Count > 0;
    }

    public sealed class ManagedDataMigrationCandidate
    {
        public string AddonId { get; set; } = string.Empty;
        public string SourcePath { get; set; } = string.Empty;
        public string TargetPath { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    public sealed class PathHealthOperationResult
    {
        public int ChangedCount { get; set; }
        public int MovedCount { get; set; }
        public int SkippedCount { get; set; }
        public IReadOnlyList<string> Messages { get; set; } = Array.Empty<string>();
    }

    public static class PathHealthService
    {
        private static readonly Regex AddonNoMountEntryRegex = new Regex("\"\\d+\"\\s+\"(?<id>\\d+)\"", RegexOptions.Compiled);

        public static void UpdatePathState(
            Configuration configuration,
            PathSnapshot currentSnapshot,
            string managerPath,
            string addonsPath)
        {
            configuration.PathState ??= new PathState();
            var state = configuration.PathState;
            var previous = state.LastDetectedSnapshot;

            state.PreviousDetectedSnapshot = previous;
            state.LastDetectedSnapshot = currentSnapshot;

            if (IsUsableSnapshot(currentSnapshot))
            {
                state.LastKnownGoodSnapshot = currentSnapshot;
            }

            if (!PathsEqual(state.LastManagerPath, managerPath))
            {
                state.PreviousManagerPath = state.LastManagerPath;
                AddChange(state, "ManagerPath", state.LastManagerPath, managerPath);
            }

            if (!PathsEqual(state.LastAddonsPath, addonsPath))
            {
                state.PreviousAddonsPath = state.LastAddonsPath;
                AddChange(state, "ManagedAddonsPath", state.LastAddonsPath, addonsPath);
            }

            AddSnapshotPathChange(state, "SteamRoot", previous?.SteamRootPath, currentSnapshot.SteamRootPath);
            AddSnapshotPathChange(state, "GModInstall", previous?.GmodInstall?.InstallPath, currentSnapshot.GmodInstall?.InstallPath);
            AddSnapshotPathChange(state, "WorkshopRoot", previous?.ActiveWorkshopRoot?.RootPath, currentSnapshot.ActiveWorkshopRoot?.RootPath);
            AddSnapshotPathChange(state, "GModCache", previous?.GmodCacheWorkshopPath, currentSnapshot.GmodCacheWorkshopPath);
            AddSnapshotPathChange(state, "AddonNoMount", previous?.AddonNoMountPath, currentSnapshot.AddonNoMountPath);

            state.LastManagerPath = managerPath;
            state.LastAddonsPath = addonsPath;

            if (state.Changes.Count > 50)
            {
                state.Changes = state.Changes
                    .OrderByDescending(change => change.DetectedAt)
                    .Take(50)
                    .OrderBy(change => change.DetectedAt)
                    .ToList();
            }
        }

        public static PathHealthReport BuildReport(
            Configuration configuration,
            PathSnapshot currentSnapshot,
            string managerPath,
            string addonsPath)
        {
            configuration.PathState ??= new PathState();
            var issues = new List<string>();
            issues.AddRange(currentSnapshot.HealthIssues);

            var previousSnapshot = configuration.PathState.PreviousDetectedSnapshot ?? configuration.PathState.LastDetectedSnapshot;
            AddPathDiffIssue(issues, "GMod install", previousSnapshot?.GmodInstall?.InstallPath, currentSnapshot.GmodInstall?.InstallPath);
            AddPathDiffIssue(issues, "Workshop root", previousSnapshot?.ActiveWorkshopRoot?.RootPath, currentSnapshot.ActiveWorkshopRoot?.RootPath);
            AddPathDiffIssue(issues, "addonnomount.txt", previousSnapshot?.AddonNoMountPath, currentSnapshot.AddonNoMountPath);

            var metadataCandidates = BuildMetadataRepairCandidates(configuration, currentSnapshot);
            var addonNoMountPlan = BuildAddonNoMountMigrationPlan(configuration, currentSnapshot);
            var managedCandidates = BuildManagedMigrationCandidates(
                configuration,
                managerPath,
                addonsPath,
                issues);

            if (metadataCandidates.Count > 0)
            {
                issues.Add($"{metadataCandidates.Count} addon metadata path(s) can be repaired to the current Workshop root.");
            }

            if (addonNoMountPlan.HasWork)
            {
                issues.Add($"{addonNoMountPlan.ToMigrateIds.Count} addonnomount entrie(s) can be copied from the previous GMod install.");
            }

            if (managedCandidates.Count > 0)
            {
                issues.Add($"{managedCandidates.Count} GAM-managed data item(s) can be moved to the current manager root.");
            }

            return new PathHealthReport
            {
                CurrentSnapshot = currentSnapshot,
                PreviousSnapshot = previousSnapshot,
                LastKnownGoodSnapshot = configuration.PathState.LastKnownGoodSnapshot,
                Issues = issues,
                MetadataRepairCandidates = metadataCandidates,
                AddonNoMountMigrationPlan = addonNoMountPlan,
                ManagedDataMigrationCandidates = managedCandidates
            };
        }

        public static PathHealthOperationResult RepairMetadata(Configuration configuration, IEnumerable<StaleMetadataRepairCandidate> candidates)
        {
            var changed = 0;
            var skipped = 0;
            var messages = new List<string>();

            foreach (var candidate in candidates)
            {
                if (!configuration.AddonMetadata.TryGetValue(candidate.AddonId, out var addon))
                {
                    skipped++;
                    continue;
                }

                if (!AddonPayloadValidator.HasValidAddonPayload(candidate.NewPath))
                {
                    skipped++;
                    messages.Add($"Skipped {candidate.AddonId}: target payload is no longer valid.");
                    continue;
                }

                addon.FolderPath = candidate.NewPath;
                addon.IsGmaFile = false;
                changed++;
            }

            return new PathHealthOperationResult
            {
                ChangedCount = changed,
                SkippedCount = skipped,
                Messages = messages
            };
        }

        public static PathHealthOperationResult MigrateAddonNoMountEntries(AddonNoMountMigrationPlan plan)
        {
            if (!plan.HasWork || string.IsNullOrWhiteSpace(plan.TargetPath))
            {
                return new PathHealthOperationResult();
            }

            var targetIds = new HashSet<string>(plan.TargetIds, StringComparer.Ordinal);
            var added = 0;
            foreach (var id in plan.ToMigrateIds)
            {
                if (targetIds.Add(id))
                {
                    added++;
                }
            }

            WriteAddonNoMountIds(plan.TargetPath, targetIds);
            return new PathHealthOperationResult { ChangedCount = added };
        }

        public static PathHealthOperationResult MigrateManagedData(
            IEnumerable<ManagedDataMigrationCandidate> candidates,
            string currentManagerPath,
            string currentAddonsPath)
        {
            var moved = 0;
            var skipped = 0;
            var messages = new List<string>();

            foreach (var candidate in candidates)
            {
                if (!IsStructurallySafeManagedMigrationCandidate(
                        candidate,
                        currentManagerPath,
                        currentAddonsPath))
                {
                    skipped++;
                    messages.Add(
                        $"Skipped {candidate.SourcePath}: the path is not an owned GAM managed-addon entry.");
                    continue;
                }

                if (candidate.IsDirectory)
                {
                    if (!Directory.Exists(candidate.SourcePath) ||
                        Directory.Exists(candidate.TargetPath) ||
                        File.Exists(candidate.TargetPath) ||
                        IsReparsePoint(candidate.SourcePath))
                    {
                        skipped++;
                        messages.Add($"Skipped {candidate.SourcePath}: source/target state changed.");
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(candidate.TargetPath)!);
                    Directory.Move(candidate.SourcePath, candidate.TargetPath);
                    moved++;
                }
                else
                {
                    if (!File.Exists(candidate.SourcePath) ||
                        Directory.Exists(candidate.TargetPath) ||
                        File.Exists(candidate.TargetPath))
                    {
                        skipped++;
                        messages.Add($"Skipped {candidate.SourcePath}: source/target state changed.");
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(candidate.TargetPath)!);
                    File.Move(candidate.SourcePath, candidate.TargetPath);
                    moved++;
                }
            }

            return new PathHealthOperationResult
            {
                MovedCount = moved,
                SkippedCount = skipped,
                Messages = messages
            };
        }

        private static List<StaleMetadataRepairCandidate> BuildMetadataRepairCandidates(Configuration configuration, PathSnapshot currentSnapshot)
        {
            var candidates = new List<StaleMetadataRepairCandidate>();
            var activeWorkshopRoot = currentSnapshot.ActiveWorkshopRoot?.RootPath;
            if (string.IsNullOrWhiteSpace(activeWorkshopRoot) || !Directory.Exists(activeWorkshopRoot))
            {
                return candidates;
            }

            foreach (var kvp in configuration.AddonMetadata)
            {
                var addonId = kvp.Key;
                var addon = kvp.Value;
                if (addon == null || addon.IsLocal || !long.TryParse(addonId, out _))
                {
                    continue;
                }

                var currentPath = Path.Combine(activeWorkshopRoot, addonId);
                if (!AddonPayloadValidator.HasValidAddonPayload(currentPath))
                {
                    continue;
                }

                if (PathsEqual(addon.FolderPath, currentPath))
                {
                    continue;
                }

                var oldHasPayload = AddonPayloadValidator.HasValidAddonPayload(addon.FolderPath);
                if (oldHasPayload && !IsUnderPath(addon.FolderPath, activeWorkshopRoot))
                {
                    continue;
                }

                candidates.Add(new StaleMetadataRepairCandidate
                {
                    AddonId = addonId,
                    OldPath = addon.FolderPath,
                    NewPath = currentPath,
                    Reason = oldHasPayload
                        ? "Metadata points to an old path while the current Workshop root has a valid payload."
                        : "Metadata points to a missing or invalid path."
                });
            }

            return candidates;
        }

        private static AddonNoMountMigrationPlan BuildAddonNoMountMigrationPlan(Configuration configuration, PathSnapshot currentSnapshot)
        {
            var targetPath = currentSnapshot.AddonNoMountPath;
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return new AddonNoMountMigrationPlan();
            }

            var sourceCandidates = new[]
            {
                configuration.PathState?.PreviousDetectedSnapshot?.AddonNoMountPath,
                configuration.PathState?.LastKnownGoodSnapshot?.AddonNoMountPath
            };

            var sourcePath = sourceCandidates
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .FirstOrDefault(path => !PathsEqual(path, targetPath) && File.Exists(path!));

            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return new AddonNoMountMigrationPlan
                {
                    TargetPath = targetPath,
                    TargetIds = ReadAddonNoMountIds(targetPath).OrderBy(id => id).ToList()
                };
            }

            var sourceIds = ReadAddonNoMountIds(sourcePath);
            var targetIds = ReadAddonNoMountIds(targetPath);
            var toMigrate = sourceIds
                .Where(id => !targetIds.Contains(id))
                .OrderBy(id => id)
                .ToList();

            return new AddonNoMountMigrationPlan
            {
                SourcePath = sourcePath,
                TargetPath = targetPath,
                SourceIds = sourceIds.OrderBy(id => id).ToList(),
                TargetIds = targetIds.OrderBy(id => id).ToList(),
                ToMigrateIds = toMigrate
            };
        }

        private static List<ManagedDataMigrationCandidate> BuildManagedMigrationCandidates(
            Configuration configuration,
            string currentManagerPath,
            string currentAddonsPath,
            List<string> issues)
        {
            var candidates = new List<ManagedDataMigrationCandidate>();
            var pathState = configuration.PathState;
            var previousManagerPath = pathState?.PreviousManagerPath;
            var previousAddonsPath = pathState?.PreviousAddonsPath;
            if ((string.IsNullOrWhiteSpace(previousManagerPath) ||
                 string.IsNullOrWhiteSpace(previousAddonsPath)) &&
                !PathsEqual(pathState?.LastAddonsPath, currentAddonsPath))
            {
                previousManagerPath = pathState?.LastManagerPath;
                previousAddonsPath = pathState?.LastAddonsPath;
            }

            if (string.IsNullOrWhiteSpace(previousManagerPath) ||
                string.IsNullOrWhiteSpace(previousAddonsPath) ||
                PathsEqual(previousAddonsPath, currentAddonsPath) ||
                !Directory.Exists(previousAddonsPath))
            {
                return candidates;
            }

            if (!IsLegacyManagedAddonsRoot(previousManagerPath, previousAddonsPath) ||
                !IsCurrentManagedAddonsRoot(currentManagerPath, currentAddonsPath))
            {
                issues.Add(
                    $"Skipped untrusted previous managed addons root: {previousAddonsPath}");
                return candidates;
            }

            foreach (var entry in SafeEnumerateFileSystemEntries(previousAddonsPath))
            {
                var name = Path.GetFileName(entry);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var isDirectory = Directory.Exists(entry);
                if (IsReparsePoint(entry))
                {
                    issues.Add($"Skipped managed migration candidate because it is a reparse point: {entry}");
                    continue;
                }

                if (!IsGamManagedEntryName(name))
                {
                    issues.Add($"Skipped unmanaged-looking data under previous manager root: {entry}");
                    continue;
                }

                var targetPath = Path.Combine(currentAddonsPath, name);
                if (File.Exists(targetPath) || Directory.Exists(targetPath))
                {
                    issues.Add($"Skipped managed migration candidate because target already exists: {targetPath}");
                    continue;
                }

                candidates.Add(new ManagedDataMigrationCandidate
                {
                    AddonId = Path.GetFileNameWithoutExtension(name),
                    SourcePath = entry,
                    TargetPath = targetPath,
                    IsDirectory = isDirectory,
                    Reason = "Entry is under the previous GAM managed addons root and target is missing."
                });
            }

            return candidates;
        }

        private static bool IsStructurallySafeManagedMigrationCandidate(
            ManagedDataMigrationCandidate? candidate,
            string currentManagerPath,
            string currentAddonsPath)
        {
            if (candidate == null ||
                string.IsNullOrWhiteSpace(candidate.AddonId) ||
                string.IsNullOrWhiteSpace(candidate.SourcePath) ||
                string.IsNullOrWhiteSpace(candidate.TargetPath))
            {
                return false;
            }

            try
            {
                var sourcePath = NormalizePath(candidate.SourcePath);
                var targetPath = NormalizePath(candidate.TargetPath);
                var sourceAddonsRoot = Path.GetDirectoryName(sourcePath);
                var targetAddonsRoot = Path.GetDirectoryName(targetPath);
                var sourceManagerRoot = string.IsNullOrWhiteSpace(sourceAddonsRoot)
                    ? null
                    : Path.GetDirectoryName(sourceAddonsRoot);
                if (string.IsNullOrWhiteSpace(sourceAddonsRoot) ||
                    string.IsNullOrWhiteSpace(targetAddonsRoot) ||
                    string.IsNullOrWhiteSpace(sourceManagerRoot) ||
                    !IsLegacyManagedAddonsRoot(sourceManagerRoot, sourceAddonsRoot) ||
                    !IsCurrentManagedAddonsRoot(currentManagerPath, currentAddonsPath) ||
                    !PathsEqual(targetAddonsRoot, currentAddonsPath))
                {
                    return false;
                }

                var sourceName = Path.GetFileName(sourcePath);
                var targetName = Path.GetFileName(targetPath);
                if (!string.Equals(sourceName, targetName, StringComparison.OrdinalIgnoreCase) ||
                    !IsGamManagedEntryName(sourceName) ||
                    !string.Equals(
                        candidate.AddonId,
                        Path.GetFileNameWithoutExtension(sourceName),
                        StringComparison.Ordinal))
                {
                    return false;
                }

                if (!PathEntryExists(sourcePath) ||
                    IsReparsePoint(sourcePath) ||
                    PathEntryExists(targetPath))
                {
                    return false;
                }

                return candidate.IsDirectory == Directory.Exists(sourcePath);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsCurrentManagedAddonsRoot(
            string? managerPath,
            string? addonsPath)
        {
            if (string.IsNullOrWhiteSpace(managerPath) ||
                string.IsNullOrWhiteSpace(addonsPath))
            {
                return false;
            }

            try
            {
                var managerRoot = NormalizePath(managerPath);
                var managedAddonsRoot = NormalizePath(addonsPath);
                if (!PathsEqual(
                        managedAddonsRoot,
                        Path.Combine(managerRoot, "addons")) ||
                    !Directory.Exists(managerRoot) ||
                    !Directory.Exists(managedAddonsRoot) ||
                    IsReparsePoint(managerRoot) ||
                    IsReparsePoint(managedAddonsRoot))
                {
                    return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsLegacyManagedAddonsRoot(
            string? managerPath,
            string? addonsPath)
        {
            if (!IsCurrentManagedAddonsRoot(managerPath, addonsPath))
            {
                return false;
            }

            try
            {
                var managerRoot = NormalizePath(managerPath!);
                if (!string.Equals(
                        Path.GetFileName(managerRoot),
                        ".addon-manager",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var workshopRoot = Path.GetDirectoryName(managerRoot);
                var contentRoot = string.IsNullOrWhiteSpace(workshopRoot)
                    ? null
                    : Path.GetDirectoryName(workshopRoot);
                var workshopContainer = string.IsNullOrWhiteSpace(contentRoot)
                    ? null
                    : Path.GetDirectoryName(contentRoot);
                var steamAppsRoot = string.IsNullOrWhiteSpace(workshopContainer)
                    ? null
                    : Path.GetDirectoryName(workshopContainer);
                return !string.IsNullOrWhiteSpace(workshopRoot) &&
                       !string.IsNullOrWhiteSpace(contentRoot) &&
                       !string.IsNullOrWhiteSpace(workshopContainer) &&
                       !string.IsNullOrWhiteSpace(steamAppsRoot) &&
                       string.Equals(
                           Path.GetFileName(workshopRoot),
                           "4000",
                           StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(
                           Path.GetFileName(contentRoot),
                           "content",
                           StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(
                           Path.GetFileName(workshopContainer),
                           "workshop",
                           StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(
                           Path.GetFileName(steamAppsRoot),
                           "steamapps",
                           StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool PathEntryExists(string path)
        {
            try
            {
                _ = File.GetAttributes(path);
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }

        public static HashSet<string> ReadAddonNoMountIds(string? path)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return result;
            }

            var text = File.ReadAllText(path, Encoding.UTF8);
            foreach (Match match in AddonNoMountEntryRegex.Matches(text))
            {
                var id = match.Groups["id"].Value;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    result.Add(id);
                }
            }

            return result;
        }

        public static void WriteAddonNoMountIds(string path, IEnumerable<string> ids)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var sorted = ids
                .Where(id => !string.IsNullOrWhiteSpace(id) && long.TryParse(id, out _))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id)
                .ToList();

            var builder = new StringBuilder();
            builder.AppendLine("\"addonnomount\"");
            builder.AppendLine("{");
            for (var i = 0; i < sorted.Count; i++)
            {
                builder.Append("\t\"")
                    .Append(i + 1)
                    .Append("\"\t\t\"")
                    .Append(sorted[i])
                    .AppendLine("\"");
            }
            builder.AppendLine("}");

            var temp = path + ".tmp";
            File.WriteAllText(temp, builder.ToString(), new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }

        private static bool IsUsableSnapshot(PathSnapshot snapshot)
        {
            var gmodInstall = snapshot.GmodInstall;
            var workshopRoot = snapshot.ActiveWorkshopRoot;
            return gmodInstall != null &&
                   gmodInstall.Confidence != PathCandidateConfidence.Rejected &&
                   PathOverrideResolver.IsDirectoryUsable(
                       Path.Combine(gmodInstall.InstallPath, "garrysmod")) &&
                   workshopRoot != null &&
                   workshopRoot.Confidence != PathCandidateConfidence.Rejected &&
                   PathOverrideResolver.IsDirectoryUsable(workshopRoot.RootPath);
        }

        private static void AddSnapshotPathChange(PathState state, string kind, string? oldPath, string? newPath)
        {
            if (!PathsEqual(oldPath, newPath))
            {
                AddChange(state, kind, oldPath, newPath);
            }
        }

        private static void AddChange(PathState state, string kind, string? oldPath, string? newPath)
        {
            if (string.IsNullOrWhiteSpace(oldPath) && string.IsNullOrWhiteSpace(newPath))
            {
                return;
            }

            if (PathsEqual(oldPath, newPath))
            {
                return;
            }

            state.Changes.Add(new PathChangeRecord
            {
                DetectedAt = DateTime.UtcNow,
                PathKind = kind,
                OldPath = oldPath,
                NewPath = newPath
            });
        }

        private static void AddPathDiffIssue(List<string> issues, string label, string? previous, string? current)
        {
            if (!string.IsNullOrWhiteSpace(previous) &&
                !string.IsNullOrWhiteSpace(current) &&
                !PathsEqual(previous, current))
            {
                issues.Add($"{label} changed: {previous} -> {current}");
            }
        }

        private static bool PathsEqual(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        private static bool IsUnderPath(string childPath, string parentPath)
        {
            if (string.IsNullOrWhiteSpace(childPath) || string.IsNullOrWhiteSpace(parentPath))
            {
                return false;
            }

            var child = NormalizePath(childPath) + Path.DirectorySeparatorChar;
            var parent = NormalizePath(parentPath) + Path.DirectorySeparatorChar;
            return child.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSafeDescendant(string childPath, string parentPath)
        {
            return IsUnderPath(childPath, parentPath);
        }

        private static bool IsReparsePoint(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }
            catch
            {
                return true;
            }
        }

        private static IEnumerable<string> SafeEnumerateFileSystemEntries(string path)
        {
            try
            {
                return Directory.EnumerateFileSystemEntries(path).ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static bool IsGamManagedEntryName(string name)
        {
            if (long.TryParse(name, out _))
            {
                return true;
            }

            if (name.EndsWith(".gma", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(Path.GetFileNameWithoutExtension(name), out _))
            {
                return true;
            }

            return false;
        }
    }
}
