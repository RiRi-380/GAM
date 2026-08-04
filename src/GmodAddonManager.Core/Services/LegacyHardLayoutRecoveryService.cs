using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace GmodAddonManager.Core.Services
{
    public enum LegacyHardLayoutRecoveryStatus
    {
        NotRequired,
        Recovered,
        Blocked,
        DeferredWhileGmodIsRunning
    }

    public sealed class LegacyHardLayoutRecoveryResult
    {
        public LegacyHardLayoutRecoveryStatus Status { get; set; }
        public int RecoveredItemCount { get; set; }
        public IReadOnlyList<string> DisabledAddonIds { get; set; } = Array.Empty<string>();
        public string? FailureCode { get; set; }
        public string? FailureDetail { get; set; }
    }

    /// <summary>
    /// Restores payloads moved by GAM v1's physical Hard mode before v2's Soft
    /// mode is allowed to inventory or write runtime state. Recovery is
    /// intentionally conservative: only exact v1 junction/hard-link shapes are
    /// removed, all conflicts block startup, and every mutation is journalled.
    /// </summary>
    public sealed class LegacyHardLayoutRecoveryService
    {
        private const int JournalSchemaVersion = 2;
        private const int LegacyJournalSchemaVersion = 1;
        private const int MaximumJournalBytes = 16 * 1024 * 1024;
        private const int MaximumRecoveryOperations = 10_000;
        private const int MaximumPayloadEntries = 500_000;
        private const int MaximumRelativePathLength = 1024;
        private const int MaximumWorkshopIdLength = 20;
        private const string JournalFileName = "legacy-hard-layout-recovery.json";

        private readonly JunctionService junctionService;

        internal Action<string>? AfterLegacyTargetRemovalBeforeJournalForTesting { get; set; }
        internal Action<string>? BeforePayloadMoveForTesting { get; set; }

        public LegacyHardLayoutRecoveryService()
            : this(new JunctionService())
        {
        }

        internal LegacyHardLayoutRecoveryService(JunctionService junctionService)
        {
            this.junctionService = junctionService ??
                throw new ArgumentNullException(nameof(junctionService));
        }

        public Task<LegacyHardLayoutRecoveryResult> RecoverIfNeededAsync(
            string workshopRootPath,
            string gmodInstallPath,
            string appDataPath,
            bool isGmodRunning,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(
                () => RecoverIfNeeded(
                    workshopRootPath,
                    gmodInstallPath,
                    appDataPath,
                    isGmodRunning,
                    cancellationToken),
                cancellationToken);
        }

        private LegacyHardLayoutRecoveryResult RecoverIfNeeded(
            string workshopRootPath,
            string gmodInstallPath,
            string appDataPath,
            bool isGmodRunning,
            CancellationToken cancellationToken)
        {
            var workshopRoot = NormalizeExistingRoot(workshopRootPath, nameof(workshopRootPath));
            var gmodRoot = NormalizeExistingRoot(gmodInstallPath, nameof(gmodInstallPath));
            var dataRoot = NormalizeOrCreateDataRoot(appDataPath);
            var cacheRoot = Path.GetFullPath(Path.Combine(
                gmodRoot,
                "garrysmod",
                "cache",
                "workshop"));
            var workshopManagerRoot = EnsureDirectDescendant(
                workshopRoot,
                Path.Combine(workshopRoot, ".addon-manager"));
            var cacheManagerRoot = EnsureDirectDescendant(
                cacheRoot,
                Path.Combine(cacheRoot, ".addon-manager"));
            var journalPath = Path.Combine(dataRoot, JournalFileName);

            LegacyRecoveryJournal? journal;
            try
            {
                journal = LoadJournal(journalPath);
                if (journal != null && !journal.Completed)
                {
                    ValidateJournalRoots(journal, workshopRoot, cacheRoot);
                }
                else
                {
                    journal = BuildJournal(
                        workshopRoot,
                        cacheRoot,
                        workshopManagerRoot,
                        cacheManagerRoot,
                        cancellationToken);
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                return Blocked("legacy_recovery_inspection_failed", ex.Message);
            }

            if (journal == null || journal.Operations.Count == 0)
            {
                return new LegacyHardLayoutRecoveryResult
                {
                    Status = LegacyHardLayoutRecoveryStatus.NotRequired
                };
            }

            if (isGmodRunning)
            {
                return new LegacyHardLayoutRecoveryResult
                {
                    Status = LegacyHardLayoutRecoveryStatus.DeferredWhileGmodIsRunning,
                    FailureCode = "legacy_recovery_gmod_running"
                };
            }

            try
            {
                PreflightRuntimeState(gmodRoot, journal.DisabledAddonIds);
                // Persist the exact preflight plan even when it replaces a
                // previously completed journal from an older recovery pass.
                SaveJournal(journalPath, journal);

                foreach (var operation in journal.Operations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!operation.Completed)
                    {
                        RecoverOperation(
                            operation,
                            cancellationToken,
                            phase =>
                            {
                                operation.Phase = phase;
                                SaveJournal(journalPath, journal);
                            });
                        operation.Phase = LegacyRecoveryOperationPhase.Completed;
                        operation.Completed = true;
                        operation.CompletedAtUtc = DateTime.UtcNow;
                        SaveJournal(journalPath, journal);
                    }
                    else
                    {
                        VerifyCompletedOperation(operation, cancellationToken);
                    }
                }

                MergeLegacyDisabledState(gmodRoot, journal.DisabledAddonIds);
                journal.Completed = true;
                journal.CompletedAtUtc = DateTime.UtcNow;
                SaveJournal(journalPath, journal);

                return new LegacyHardLayoutRecoveryResult
                {
                    Status = LegacyHardLayoutRecoveryStatus.Recovered,
                    RecoveredItemCount = journal.Operations.Count,
                    DisabledAddonIds = journal.DisabledAddonIds
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .ToArray()
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Blocked("legacy_recovery_mutation_failed", ex.Message);
            }

        }

        private LegacyRecoveryJournal? BuildJournal(
            string workshopRoot,
            string cacheRoot,
            string workshopManagerRoot,
            string cacheManagerRoot,
            CancellationToken cancellationToken)
        {
            var operations = new List<LegacyRecoveryOperation>();
            var disabled = new HashSet<string>(StringComparer.Ordinal);
            var workshopManagedRoot = Path.Combine(workshopManagerRoot, "addons");
            EnsureOwnedManagerDirectoryIsSafe(
                workshopManagerRoot,
                "Workshop .addon-manager root");
            EnsureOwnedManagerDirectoryIsSafe(
                workshopManagedRoot,
                "Workshop managed addons root");

            if (Directory.Exists(workshopManagedRoot))
            {
                foreach (var source in Directory.EnumerateDirectories(workshopManagedRoot))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var addonId = Path.GetFileName(source);
                    if (!IsWorkshopId(addonId))
                    {
                        continue;
                    }

                    var target = Path.Combine(workshopRoot, addonId);
                    var operation = InspectWorkshopOperation(addonId, source, target, cancellationToken);
                    if (operation.Fingerprint.EntryCount == 0)
                    {
                        // A stale empty manager folder is not an addon payload.
                        continue;
                    }
                    EnsureRecoveryOperationBudget(operations.Count);
                    operations.Add(operation);
                    if (!operation.WasEnabled)
                    {
                        disabled.Add(addonId);
                    }
                }
            }

            var cacheManagedRoot = Path.Combine(cacheManagerRoot, "addons");
            EnsureOwnedManagerDirectoryIsSafe(
                cacheManagerRoot,
                "GMod cache .addon-manager root");
            EnsureOwnedManagerDirectoryIsSafe(
                cacheManagedRoot,
                "GMod cache managed addons root");
            if (Directory.Exists(cacheManagedRoot))
            {
                foreach (var source in Directory.EnumerateFiles(cacheManagedRoot, "*.gma"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var addonId = Path.GetFileNameWithoutExtension(source);
                    if (!IsWorkshopId(addonId))
                    {
                        continue;
                    }

                    var target = Path.Combine(cacheRoot, Path.GetFileName(source));
                    var operation = InspectCacheOperation(addonId, source, target, cancellationToken);
                    if (operation.Fingerprint.TotalBytes == 0)
                    {
                        // Do not resurrect zero-byte cache remnants as addons.
                        continue;
                    }
                    EnsureRecoveryOperationBudget(operations.Count);
                    operations.Add(operation);

                    var workshopTarget = Path.Combine(workshopRoot, addonId);
                    if (!operation.WasEnabled && !Directory.Exists(workshopTarget))
                    {
                        disabled.Add(addonId);
                    }
                }
            }

            if (operations.Count == 0)
            {
                return null;
            }

            return new LegacyRecoveryJournal
            {
                SchemaVersion = JournalSchemaVersion,
                WorkshopRoot = workshopRoot,
                CacheRoot = cacheRoot,
                CreatedAtUtc = DateTime.UtcNow,
                DisabledAddonIds = disabled.OrderBy(id => id, StringComparer.Ordinal).ToList(),
                Operations = operations
            };
        }

        private static void EnsureRecoveryOperationBudget(int currentCount)
        {
            if (currentCount >= MaximumRecoveryOperations)
            {
                throw new InvalidDataException(
                    $"Legacy recovery exceeds the {MaximumRecoveryOperations}-operation safety limit.");
            }
        }

        private LegacyRecoveryOperation InspectWorkshopOperation(
            string addonId,
            string source,
            string target,
            CancellationToken cancellationToken)
        {
            var fingerprint = CreatePayloadFingerprint(source, cancellationToken);
            if (!PathEntryExists(target))
            {
                return CreateOperation(
                    LegacyRecoveryOperationKind.WorkshopDirectory,
                    addonId,
                    source,
                    target,
                    wasEnabled: false,
                    LegacyRecoveryTargetShape.Absent,
                    fingerprint);
            }

            if (!Directory.Exists(target))
            {
                throw new InvalidDataException(
                    $"Legacy Workshop target is not a directory for addon {addonId}.");
            }

            if (junctionService.IsJunction(target))
            {
                var actualTarget = Path.GetFullPath(junctionService.GetJunctionTarget(target));
                if (!PathsEqual(actualTarget, source))
                {
                    throw new InvalidDataException(
                        $"Legacy Workshop junction for addon {addonId} points outside its managed payload.");
                }

                return CreateOperation(
                    LegacyRecoveryOperationKind.WorkshopDirectory,
                    addonId,
                    source,
                    target,
                    wasEnabled: true,
                    LegacyRecoveryTargetShape.Junction,
                    fingerprint);
            }

            var expectedManagedGma = Path.Combine(source, addonId + ".gma");
            var expectedWorkshopGma = Path.Combine(target, addonId + ".gma");
            var entries = Directory.EnumerateFileSystemEntries(target).ToArray();
            if (entries.Length == 1 &&
                File.Exists(expectedManagedGma) &&
                File.Exists(expectedWorkshopGma) &&
                junctionService.IsHardLink(expectedWorkshopGma, expectedManagedGma))
            {
                return CreateOperation(
                    LegacyRecoveryOperationKind.WorkshopDirectory,
                    addonId,
                    source,
                    target,
                    wasEnabled: true,
                    LegacyRecoveryTargetShape.HardLinkDirectory,
                    fingerprint);
            }

            throw new InvalidDataException(
                $"A non-legacy Workshop payload already exists for addon {addonId}; no files were overwritten.");
        }

        private LegacyRecoveryOperation InspectCacheOperation(
            string addonId,
            string source,
            string target,
            CancellationToken cancellationToken)
        {
            var fingerprint = CreatePayloadFingerprint(source, cancellationToken);
            if (!PathEntryExists(target))
            {
                return CreateOperation(
                    LegacyRecoveryOperationKind.CacheGma,
                    addonId,
                    source,
                    target,
                    wasEnabled: false,
                    LegacyRecoveryTargetShape.Absent,
                    fingerprint);
            }

            if (File.Exists(target) && junctionService.IsHardLink(target, source))
            {
                return CreateOperation(
                    LegacyRecoveryOperationKind.CacheGma,
                    addonId,
                    source,
                    target,
                    wasEnabled: true,
                    LegacyRecoveryTargetShape.HardLinkFile,
                    fingerprint);
            }

            throw new InvalidDataException(
                $"A non-legacy GMod cache payload already exists for addon {addonId}; no files were overwritten.");
        }

        private static LegacyRecoveryOperation CreateOperation(
            LegacyRecoveryOperationKind kind,
            string addonId,
            string source,
            string target,
            bool wasEnabled,
            LegacyRecoveryTargetShape targetShape,
            LegacyPayloadFingerprint fingerprint)
        {
            return new LegacyRecoveryOperation
            {
                Kind = kind,
                AddonId = addonId,
                SourcePath = Path.GetFullPath(source),
                TargetPath = Path.GetFullPath(target),
                WasEnabled = wasEnabled,
                TargetShape = targetShape,
                Fingerprint = fingerprint
            };
        }

        private void RecoverOperation(
            LegacyRecoveryOperation operation,
            CancellationToken cancellationToken,
            Action<LegacyRecoveryOperationPhase> persistPhase)
        {
            if (!SourceExists(operation))
            {
                VerifyCompletedOperation(operation, cancellationToken);
                persistPhase(LegacyRecoveryOperationPhase.PayloadMoved);
                return;
            }

            var currentFingerprint = CreatePayloadFingerprint(operation.SourcePath, cancellationToken);
            if (!operation.Fingerprint.Equals(currentFingerprint))
            {
                throw new InvalidDataException(
                    $"Legacy payload changed after inspection for addon {operation.AddonId}.");
            }

            var sourceRoot = Path.GetPathRoot(operation.SourcePath);
            var targetRoot = Path.GetPathRoot(operation.TargetPath);
            if (!string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    $"Legacy payload for addon {operation.AddonId} is not on the same volume as its destination.");
            }

            if (operation.Phase < LegacyRecoveryOperationPhase.TargetRemoved)
            {
                var removedLegacyTarget = false;
                if (TargetExists(operation))
                {
                    RemoveExpectedLegacyTarget(operation);
                    removedLegacyTarget = true;
                }

                if (removedLegacyTarget)
                {
                    AfterLegacyTargetRemovalBeforeJournalForTesting?.Invoke(
                        operation.AddonId);
                }

                // A process can terminate after removing the exact v1 link but
                // before this phase is persisted. Source fingerprint equality
                // plus an absent target is therefore a safe, idempotent resume
                // state: no unrelated payload can be overwritten.
                if (TargetExists(operation))
                {
                    throw new IOException(
                        $"Legacy recovery target is still occupied for addon {operation.AddonId}.");
                }

                persistPhase(LegacyRecoveryOperationPhase.TargetRemoved);
            }
            else if (TargetExists(operation))
            {
                throw new IOException(
                    $"A target appeared after legacy link removal for addon {operation.AddonId}.");
            }

            BeforePayloadMoveForTesting?.Invoke(operation.AddonId);

            if (operation.Kind == LegacyRecoveryOperationKind.WorkshopDirectory)
            {
                Directory.Move(operation.SourcePath, operation.TargetPath);
            }
            else
            {
                File.Move(operation.SourcePath, operation.TargetPath);
            }

            VerifyCompletedOperation(operation, cancellationToken);
            persistPhase(LegacyRecoveryOperationPhase.PayloadMoved);
        }

        private void RemoveExpectedLegacyTarget(LegacyRecoveryOperation operation)
        {
            switch (operation.TargetShape)
            {
                case LegacyRecoveryTargetShape.Absent:
                    if (TargetExists(operation))
                    {
                        throw new IOException(
                            $"A target appeared during legacy recovery for addon {operation.AddonId}.");
                    }
                    break;

                case LegacyRecoveryTargetShape.Junction:
                    if (!Directory.Exists(operation.TargetPath) ||
                        !junctionService.IsJunction(operation.TargetPath) ||
                        !PathsEqual(
                            junctionService.GetJunctionTarget(operation.TargetPath),
                            operation.SourcePath))
                    {
                        throw new InvalidDataException(
                            $"Legacy junction changed during recovery for addon {operation.AddonId}.");
                    }

                    junctionService.RemoveJunction(operation.TargetPath);
                    break;

                case LegacyRecoveryTargetShape.HardLinkDirectory:
                    var managedGma = Path.Combine(
                        operation.SourcePath,
                        operation.AddonId + ".gma");
                    var workshopGma = Path.Combine(
                        operation.TargetPath,
                        operation.AddonId + ".gma");
                    if (!Directory.Exists(operation.TargetPath) ||
                        Directory.EnumerateFileSystemEntries(operation.TargetPath).Count() != 1 ||
                        !File.Exists(workshopGma) ||
                        !File.Exists(managedGma) ||
                        !junctionService.IsHardLink(workshopGma, managedGma))
                    {
                        throw new InvalidDataException(
                            $"Legacy Workshop hard link changed during recovery for addon {operation.AddonId}.");
                    }

                    File.Delete(workshopGma);
                    Directory.Delete(operation.TargetPath, recursive: false);
                    break;

                case LegacyRecoveryTargetShape.HardLinkFile:
                    if (!File.Exists(operation.TargetPath) ||
                        !File.Exists(operation.SourcePath) ||
                        !junctionService.IsHardLink(operation.TargetPath, operation.SourcePath))
                    {
                        throw new InvalidDataException(
                            $"Legacy cache hard link changed during recovery for addon {operation.AddonId}.");
                    }

                    File.Delete(operation.TargetPath);
                    break;

                default:
                    throw new InvalidDataException("Unknown legacy recovery target shape.");
            }
        }

        private static void VerifyCompletedOperation(
            LegacyRecoveryOperation operation,
            CancellationToken cancellationToken)
        {
            if (SourceExists(operation) || !TargetExists(operation))
            {
                throw new IOException(
                    $"Legacy recovery is incomplete for addon {operation.AddonId}.");
            }

            var targetFingerprint = CreatePayloadFingerprint(operation.TargetPath, cancellationToken);
            if (!operation.Fingerprint.Equals(targetFingerprint))
            {
                throw new InvalidDataException(
                    $"Recovered payload verification failed for addon {operation.AddonId}.");
            }
        }

        private static void PreflightRuntimeState(
            string gmodRoot,
            IReadOnlyCollection<string> disabledIds)
        {
            if (disabledIds.Count == 0)
            {
                return;
            }

            var stateStore = new GmodAddonStateStore(gmodRoot);
            var snapshot = stateStore.ReadSnapshot();
            if (!snapshot.IsValidFormat)
            {
                throw new InvalidDataException(
                    "addonnomount.txt is malformed; legacy disabled state cannot be merged safely.");
            }
        }

        private static void MergeLegacyDisabledState(
            string gmodRoot,
            IReadOnlyCollection<string> disabledIds)
        {
            if (disabledIds.Count == 0)
            {
                return;
            }

            var requestedStates = disabledIds.ToDictionary(
                id => id,
                _ => false,
                StringComparer.Ordinal);
            if (!new GmodAddonStateStore(gmodRoot).SetEnabledBulk(requestedStates))
            {
                throw new IOException(
                    "Failed to merge legacy disabled addon state into addonnomount.txt.");
            }
        }

        private static LegacyPayloadFingerprint CreatePayloadFingerprint(
            string path,
            CancellationToken cancellationToken)
        {
            var rootAttributes = File.GetAttributes(path);
            if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "Legacy managed payload root is an unexpected reparse point.");
            }

            if ((rootAttributes & FileAttributes.Directory) == 0)
            {
                var file = new FileInfo(path);
                return new LegacyPayloadFingerprint
                {
                    EntryCount = 1,
                    TotalBytes = file.Length,
                    StructureHash = ComputeStructureHash(new[]
                    {
                        Path.GetFileName(path) + "\0" + file.Length
                    })
                };
            }

            if (!Directory.Exists(path))
            {
                throw new FileNotFoundException("Legacy payload disappeared during inspection.", path);
            }

            var root = Path.GetFullPath(path);
            var pending = new Stack<string>();
            var entries = new List<string>();
            long totalBytes = 0;
            pending.Push(root);

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = pending.Pop();
                foreach (var child in Directory.EnumerateFileSystemEntries(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidDataException(
                            "Legacy managed payload contains an unexpected reparse point.");
                    }

                    var relative = GetRelativePath(root, child);
                    if (relative.Length > MaximumRelativePathLength)
                    {
                        throw new InvalidDataException(
                            "Legacy managed payload contains an excessively long relative path.");
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        entries.Add("D\0" + relative);
                        pending.Push(child);
                    }
                    else
                    {
                        var length = new FileInfo(child).Length;
                        checked
                        {
                            totalBytes += length;
                        }
                        entries.Add("F\0" + relative + "\0" + length);
                    }

                    if (entries.Count > MaximumPayloadEntries)
                    {
                        throw new InvalidDataException(
                            "Legacy managed payload contains too many filesystem entries.");
                    }
                }
            }

            entries.Sort(StringComparer.Ordinal);
            return new LegacyPayloadFingerprint
            {
                EntryCount = entries.Count,
                TotalBytes = totalBytes,
                StructureHash = ComputeStructureHash(entries)
            };
        }

        private static string ComputeStructureHash(IEnumerable<string> entries)
        {
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(string.Join("\n", entries));
            return BytesToHex(sha.ComputeHash(bytes));
        }

        private static string BytesToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var value in bytes)
            {
                builder.Append(value.ToString("x2"));
            }
            return builder.ToString();
        }

        private static string GetRelativePath(string root, string path)
        {
            var normalizedRoot = root.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var rootUri = new Uri(normalizedRoot);
            var pathUri = new Uri(Path.GetFullPath(path));
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString())
                .Replace('/', Path.DirectorySeparatorChar);
        }

        private static bool SourceExists(LegacyRecoveryOperation operation)
        {
            return operation.Kind == LegacyRecoveryOperationKind.WorkshopDirectory
                ? Directory.Exists(operation.SourcePath)
                : File.Exists(operation.SourcePath);
        }

        private static bool TargetExists(LegacyRecoveryOperation operation)
        {
            return PathEntryExists(operation.TargetPath);
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

        private static bool IsWorkshopId(string? value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value.Length <= MaximumWorkshopIdLength &&
                   value.All(character => character >= '0' && character <= '9') &&
                   ulong.TryParse(
                       value,
                       System.Globalization.NumberStyles.None,
                       System.Globalization.CultureInfo.InvariantCulture,
                       out var parsed) &&
                   parsed > 0;
        }

        private static string NormalizeExistingRoot(string path, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Path is required.", parameterName);
            }

            var fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath))
            {
                throw new DirectoryNotFoundException(fullPath);
            }

            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string NormalizeOrCreateDataRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("App data path is required.", nameof(path));
            }

            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(fullPath);
            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string EnsureDirectDescendant(string root, string candidate)
        {
            var normalizedRoot = root.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullCandidate = Path.GetFullPath(candidate);
            if (!fullCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Legacy manager path escaped its expected root.");
            }
            return fullCandidate;
        }

        private static bool PathsEqual(string left, string right)
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        private static LegacyRecoveryJournal? LoadJournal(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var info = new FileInfo(path);
            if (info.Length > MaximumJournalBytes)
            {
                throw new InvalidDataException("Legacy recovery journal is unexpectedly large.");
            }

            var journal = JsonConvert.DeserializeObject<LegacyRecoveryJournal>(
                File.ReadAllText(path, Encoding.UTF8));
            if (journal == null ||
                (journal.SchemaVersion != JournalSchemaVersion &&
                 journal.SchemaVersion != LegacyJournalSchemaVersion))
            {
                throw new InvalidDataException("Legacy recovery journal is invalid or unsupported.");
            }

            journal.Operations ??= new List<LegacyRecoveryOperation>();
            journal.DisabledAddonIds ??= new List<string>();
            foreach (var operation in journal.Operations)
            {
                if (operation.Completed)
                {
                    operation.Phase = LegacyRecoveryOperationPhase.Completed;
                }
            }
            journal.SchemaVersion = JournalSchemaVersion;
            return journal;
        }

        private static void ValidateJournalRoots(
            LegacyRecoveryJournal journal,
            string workshopRoot,
            string cacheRoot)
        {
            if (!PathsEqual(journal.WorkshopRoot, workshopRoot) ||
                !PathsEqual(journal.CacheRoot, cacheRoot))
            {
                throw new InvalidDataException(
                    "An incomplete legacy recovery journal belongs to different GMod paths.");
            }

            EnsureOwnedManagerDirectoryIsSafe(
                Path.Combine(workshopRoot, ".addon-manager"),
                "Workshop .addon-manager root");
            EnsureOwnedManagerDirectoryIsSafe(
                Path.Combine(workshopRoot, ".addon-manager", "addons"),
                "Workshop managed addons root");
            EnsureOwnedManagerDirectoryIsSafe(
                Path.Combine(cacheRoot, ".addon-manager"),
                "GMod cache .addon-manager root");
            EnsureOwnedManagerDirectoryIsSafe(
                Path.Combine(cacheRoot, ".addon-manager", "addons"),
                "GMod cache managed addons root");

            if (journal.Operations.Count > MaximumRecoveryOperations ||
                journal.DisabledAddonIds.Count > journal.Operations.Count)
            {
                throw new InvalidDataException(
                    "Legacy recovery journal exceeds its operation safety limit.");
            }

            var operationKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var operation in journal.Operations)
            {
                if (!IsWorkshopId(operation.AddonId) ||
                    operation.Fingerprint == null ||
                    string.IsNullOrWhiteSpace(operation.SourcePath) ||
                    string.IsNullOrWhiteSpace(operation.TargetPath) ||
                    !Enum.IsDefined(typeof(LegacyRecoveryOperationKind), operation.Kind) ||
                    !Enum.IsDefined(typeof(LegacyRecoveryTargetShape), operation.TargetShape) ||
                    !Enum.IsDefined(typeof(LegacyRecoveryOperationPhase), operation.Phase) ||
                    (!operation.Completed &&
                     operation.Phase == LegacyRecoveryOperationPhase.Completed) ||
                    operation.Fingerprint.EntryCount < 0 ||
                    operation.Fingerprint.EntryCount > MaximumPayloadEntries ||
                    operation.Fingerprint.TotalBytes < 0 ||
                    !IsSha256Hex(operation.Fingerprint.StructureHash))
                {
                    throw new InvalidDataException("Legacy recovery journal contains an invalid operation.");
                }

                var isWorkshop = operation.Kind ==
                    LegacyRecoveryOperationKind.WorkshopDirectory;
                var expectedSource = isWorkshop
                    ? Path.Combine(
                        workshopRoot,
                        ".addon-manager",
                        "addons",
                        operation.AddonId)
                    : Path.Combine(
                        cacheRoot,
                        ".addon-manager",
                        "addons",
                        operation.AddonId + ".gma");
                var expectedTarget = isWorkshop
                    ? Path.Combine(workshopRoot, operation.AddonId)
                    : Path.Combine(cacheRoot, operation.AddonId + ".gma");
                if (!PathsEqual(operation.SourcePath, expectedSource) ||
                    !PathsEqual(operation.TargetPath, expectedTarget) ||
                    !IsCompatibleTargetShape(operation.Kind, operation.TargetShape) ||
                    operation.WasEnabled !=
                        (operation.TargetShape != LegacyRecoveryTargetShape.Absent) ||
                    !operationKeys.Add(operation.Kind + ":" + operation.AddonId))
                {
                    throw new InvalidDataException(
                        "Legacy recovery journal operation does not match an exact v1 path.");
                }
            }

            var disabledIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var addonId in journal.DisabledAddonIds)
            {
                if (!IsWorkshopId(addonId) ||
                    !disabledIds.Add(addonId) ||
                    !journal.Operations.Any(operation =>
                        string.Equals(operation.AddonId, addonId, StringComparison.Ordinal) &&
                        !operation.WasEnabled))
                {
                    throw new InvalidDataException(
                        "Legacy recovery journal contains an invalid disabled-addon entry.");
                }
            }
        }

        private static bool IsCompatibleTargetShape(
            LegacyRecoveryOperationKind kind,
            LegacyRecoveryTargetShape shape)
        {
            return kind == LegacyRecoveryOperationKind.WorkshopDirectory
                ? shape == LegacyRecoveryTargetShape.Absent ||
                  shape == LegacyRecoveryTargetShape.Junction ||
                  shape == LegacyRecoveryTargetShape.HardLinkDirectory
                : shape == LegacyRecoveryTargetShape.Absent ||
                  shape == LegacyRecoveryTargetShape.HardLinkFile;
        }

        private static bool IsSha256Hex(string? value)
        {
            return value != null &&
                   value.Length == 64 &&
                   value.All(character =>
                       (character >= '0' && character <= '9') ||
                       (character >= 'a' && character <= 'f') ||
                       (character >= 'A' && character <= 'F'));
        }

        private static void EnsureDescendant(string root, string candidate)
        {
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullCandidate = Path.GetFullPath(candidate);
            if (!fullCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Legacy recovery journal contains an out-of-root path.");
            }
        }

        private static void EnsureOwnedManagerDirectoryIsSafe(
            string path,
            string description)
        {
            if (!PathEntryExists(path))
            {
                return;
            }

            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"{description} is not a normal directory; legacy recovery was refused.");
            }
        }

        private static void SaveJournal(string path, LegacyRecoveryJournal journal)
        {
            var temp = path + ".tmp";
            var json = JsonConvert.SerializeObject(journal, Formatting.Indented);
            if (Encoding.UTF8.GetByteCount(json) > MaximumJournalBytes)
            {
                throw new InvalidDataException(
                    "Legacy recovery journal exceeds its byte safety limit.");
            }
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Replace(temp, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, path);
            }
        }

        private static LegacyHardLayoutRecoveryResult Blocked(string code, string detail)
        {
            return new LegacyHardLayoutRecoveryResult
            {
                Status = LegacyHardLayoutRecoveryStatus.Blocked,
                FailureCode = code,
                FailureDetail = detail
            };
        }

        private enum LegacyRecoveryOperationKind
        {
            WorkshopDirectory,
            CacheGma
        }

        private enum LegacyRecoveryTargetShape
        {
            Absent,
            Junction,
            HardLinkDirectory,
            HardLinkFile
        }

        private enum LegacyRecoveryOperationPhase
        {
            Planned,
            TargetRemoved,
            PayloadMoved,
            Completed
        }

        private sealed class LegacyRecoveryJournal
        {
            public int SchemaVersion { get; set; }
            public string WorkshopRoot { get; set; } = string.Empty;
            public string CacheRoot { get; set; } = string.Empty;
            public DateTime CreatedAtUtc { get; set; }
            public DateTime? CompletedAtUtc { get; set; }
            public bool Completed { get; set; }
            public List<string> DisabledAddonIds { get; set; } = new List<string>();
            public List<LegacyRecoveryOperation> Operations { get; set; } =
                new List<LegacyRecoveryOperation>();
        }

        private sealed class LegacyRecoveryOperation
        {
            public LegacyRecoveryOperationKind Kind { get; set; }
            public string AddonId { get; set; } = string.Empty;
            public string SourcePath { get; set; } = string.Empty;
            public string TargetPath { get; set; } = string.Empty;
            public bool WasEnabled { get; set; }
            public LegacyRecoveryTargetShape TargetShape { get; set; }
            public LegacyPayloadFingerprint Fingerprint { get; set; } =
                new LegacyPayloadFingerprint();
            public LegacyRecoveryOperationPhase Phase { get; set; }
            public bool Completed { get; set; }
            public DateTime? CompletedAtUtc { get; set; }
        }

        private sealed class LegacyPayloadFingerprint : IEquatable<LegacyPayloadFingerprint>
        {
            public int EntryCount { get; set; }
            public long TotalBytes { get; set; }
            public string StructureHash { get; set; } = string.Empty;

            public bool Equals(LegacyPayloadFingerprint? other)
            {
                return other != null &&
                       EntryCount == other.EntryCount &&
                       TotalBytes == other.TotalBytes &&
                       string.Equals(
                           StructureHash,
                           other.StructureHash,
                           StringComparison.Ordinal);
            }

            public override bool Equals(object? obj)
            {
                return Equals(obj as LegacyPayloadFingerprint);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = EntryCount;
                    hash = (hash * 397) ^ TotalBytes.GetHashCode();
                    hash = (hash * 397) ^ (StructureHash?.GetHashCode() ?? 0);
                    return hash;
                }
            }
        }
    }
}
