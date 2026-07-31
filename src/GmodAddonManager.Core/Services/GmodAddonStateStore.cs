using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using GmodAddonManager.Core.Models;

namespace GmodAddonManager.Core.Services
{
    /// <summary>
    /// Manages Garry's Mod addon enable/disable state by editing garrysmod/cfg/addonnomount.txt.
    /// Addons listed in this file are DISABLED (not mounted).
    /// Addons NOT in this file are ENABLED.
    /// </summary>
    public class GmodAddonStateStore
    {
        private const int StableReadAttempts = 5;
        private const int StableReadDelayMs = 75;
        private const int MergeAttempts = 5;
        private static readonly TimeSpan PathMutexTimeout = TimeSpan.FromSeconds(10);

        private readonly string noMountFilePath;
        private readonly string pathMutexName;
        private readonly object fileLock = new object();

        internal Action? BeforeMergeCommitForTesting { get; set; }
        internal Action<int>? DuringStableReadForTesting { get; set; }

        public GmodAddonStateStore(string gmodRootPath)
        {
            if (string.IsNullOrWhiteSpace(gmodRootPath))
            {
                throw new ArgumentException("gmodRootPath is null or empty", nameof(gmodRootPath));
            }

            var cfgDir = Path.Combine(gmodRootPath, "garrysmod", "cfg");
            noMountFilePath = Path.Combine(cfgDir, "addonnomount.txt");
            pathMutexName = BuildPathMutexName(noMountFilePath);
        }

        public string NoMountFilePath => noMountFilePath;

        /// <summary>
        /// Set enable state for a single workshop addon id. Returns true if the state was persisted.
        /// </summary>
        public bool SetEnabled(string workshopId, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(workshopId)) return false;

            lock (fileLock)
            {
                return ExecuteWithPathMutexNoLock(
                    () => TryMergeEnabledStatesNoLock(
                        new Dictionary<string, bool>(StringComparer.Ordinal)
                        {
                            [workshopId] = enabled
                        }));
            }
        }

        /// <summary>
        /// Bulk set multiple states atomically. Returns true if all requested states were persisted.
        /// </summary>
        public bool SetEnabledBulk(Dictionary<string, bool> statesToApply)
        {
            if (statesToApply == null || statesToApply.Count == 0) return true;

            lock (fileLock)
            {
                return ExecuteWithPathMutexNoLock(
                    () => TryMergeEnabledStatesNoLock(statesToApply));
            }
        }

        public AddonMountSnapshot ReadSnapshot()
        {
            lock (fileLock)
            {
                return ReadSnapshotNoLock();
            }
        }

        public AddonMountSnapshot WriteDisabledIds(IEnumerable<string> disabledIds)
        {
            lock (fileLock)
            {
                AddonMountSnapshot? snapshot = null;
                var saved = ExecuteWithPathMutexNoLock(() =>
                {
                    var normalized = new HashSet<string>(
                        NormalizeDisabledIds(disabledIds),
                        StringComparer.Ordinal);
                    if (!SaveDisabledIdsNoLock(normalized))
                    {
                        return false;
                    }

                    snapshot = ReadSnapshotNoLock();
                    return true;
                });
                if (!saved || snapshot == null)
                {
                    throw new IOException(
                        "Failed to persist addonnomount.txt.");
                }

                return snapshot;
            }
        }

        /// <summary>
        /// Get enabled state for a single addon. Returns null if unknown.
        /// </summary>
        public bool? GetEnabled(string workshopId)
        {
            if (string.IsNullOrWhiteSpace(workshopId)) return null;
            lock (fileLock)
            {
                try
                {
                    var snapshot = ReadSnapshotNoLock();
                    if (!snapshot.IsValidFormat)
                    {
                        return null;
                    }

                    return !snapshot.DisabledIds.Contains(workshopId, StringComparer.Ordinal);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[GmodAddonStateStore] Failed to read addon state: {ex.Message}");
                    return null;
                }
            }
        }

        /// <summary>
        /// Get all addon states. Returns true for enabled, false for disabled.
        /// Note: Only returns states for addons that are explicitly disabled.
        /// Addons not in the list are assumed enabled.
        /// </summary>
        public IReadOnlyDictionary<string, bool> GetAllStates()
        {
            lock (fileLock)
            {
                var disabledIds = LoadDisabledIdsNoLock();
                var result = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var id in disabledIds)
                {
                    result[id] = false;
                }
                return result;
            }
        }

        /// <summary>
        /// Get all disabled addon IDs.
        /// </summary>
        public HashSet<string> GetDisabledIds()
        {
            lock (fileLock)
            {
                return LoadDisabledIdsNoLock();
            }
        }

        public static List<string> ParseDisabledIds(string text)
        {
            return TryParseNoMountDocument(text, out var disabledIds)
                ? disabledIds
                : new List<string>();
        }

        public static List<string> NormalizeDisabledIds(IEnumerable<string>? ids)
        {
            if (ids == null)
            {
                return new List<string>();
            }

            return ids
                .Where(IsNormalizedWorkshopId)
                .Select(NormalizeWorkshopId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
        }

        public static string ComputeSemanticHash(IEnumerable<string>? disabledIds)
        {
            var normalized = string.Join("\n", NormalizeDisabledIds(disabledIds));
            return ComputeSha256Hex(Encoding.UTF8.GetBytes(normalized));
        }

        private AddonMountSnapshot ReadSnapshotNoLock()
        {
            var read = ReadStableFileNoLock();
            var text = read.Bytes.Length == 0 ? string.Empty : Encoding.UTF8.GetString(read.Bytes);
            var isValidFormat = TryParseNoMountDocument(text, out var disabledIds);

            return new AddonMountSnapshot
            {
                Path = noMountFilePath,
                DisabledIds = disabledIds,
                SemanticHash = ComputeSemanticHash(disabledIds),
                PhysicalHash = ComputeSha256Hex(read.Bytes),
                FileLastWriteUtc = read.LastWriteUtc,
                FileSize = read.FileSize,
                FileExists = read.FileSize.HasValue,
                IsValidFormat = isValidFormat,
                ObservedAtUtc = DateTime.UtcNow
            };
        }

        private static bool TryParseNoMountDocument(
            string? text,
            out List<string> disabledIds)
        {
            disabledIds = new List<string>();
            if (string.IsNullOrEmpty(text) ||
                text.All(character =>
                    char.IsWhiteSpace(character) ||
                    character == '\uFEFF'))
            {
                return true;
            }

            var parser = new NoMountDocumentParser(text);
            if (!parser.TrySkipTrivia(out _) ||
                !parser.TryReadQuotedToken(out var rootName) ||
                !string.Equals(rootName, "addonnomount", StringComparison.OrdinalIgnoreCase) ||
                !parser.TrySkipTrivia(out _) ||
                !parser.TryConsume('{'))
            {
                return false;
            }

            var parsedIds = new List<string>();
            var parsedEntry = false;
            while (true)
            {
                if (!parser.TrySkipTrivia(out var hadSeparator))
                {
                    return false;
                }

                if (parser.TryConsume('}'))
                {
                    break;
                }

                if (parsedEntry && !hadSeparator)
                {
                    return false;
                }

                if (!parser.TryReadQuotedToken(out var index) ||
                    !ulong.TryParse(index, out _) ||
                    !parser.TrySkipTrivia(out var hadKeyValueSeparator) ||
                    !hadKeyValueSeparator ||
                    !parser.TryReadQuotedToken(out var workshopId) ||
                    !IsNormalizedWorkshopId(workshopId))
                {
                    return false;
                }

                parsedIds.Add(NormalizeWorkshopId(workshopId));
                parsedEntry = true;
            }

            if (!parser.TrySkipTrivia(out _) || !parser.IsAtEnd)
            {
                return false;
            }

            disabledIds = NormalizeDisabledIds(parsedIds);
            return true;
        }

        private HashSet<string> LoadDisabledIdsNoLock()
        {
            try
            {
                var snapshot = ReadSnapshotNoLock();
                return new HashSet<string>(snapshot.DisabledIds, StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GmodAddonStateStore] Failed to load addonnomount.txt: {ex.Message}");
                return new HashSet<string>(StringComparer.Ordinal);
            }
        }

        private bool TryMergeEnabledStatesNoLock(
            IReadOnlyDictionary<string, bool> statesToApply)
        {
            var requestedStates = statesToApply
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
                .ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value,
                    StringComparer.Ordinal);
            if (requestedStates.Count == 0)
            {
                return true;
            }

            for (var attempt = 0; attempt < MergeAttempts; attempt++)
            {
                if (!TryLoadDisabledIdsForMergeNoLock(
                        out var disabledIds,
                        out var sourceFingerprint))
                {
                    return false;
                }

                foreach (var entry in requestedStates)
                {
                    if (entry.Value)
                    {
                        disabledIds.Remove(entry.Key);
                    }
                    else
                    {
                        disabledIds.Add(entry.Key);
                    }
                }

                BeforeMergeCommitForTesting?.Invoke();

                StableFileRead currentRead;
                try
                {
                    currentRead = ReadStableFileNoLock();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[GmodAddonStateStore] Failed to recheck addonnomount.txt before merge: {ex.Message}");
                    return false;
                }

                if (!sourceFingerprint.Equals(currentRead.Fingerprint))
                {
                    continue;
                }

                if (!SaveDisabledIdsNoLock(disabledIds) ||
                    !TryLoadDisabledIdsForMergeNoLock(
                        out var persistedIds,
                        out _))
                {
                    return false;
                }

                foreach (var entry in requestedStates)
                {
                    var isNowDisabled = persistedIds.Contains(entry.Key);
                    if (isNowDisabled != !entry.Value)
                    {
                        return false;
                    }
                }

                return true;
            }

            System.Diagnostics.Debug.WriteLine(
                "[GmodAddonStateStore] Refusing to overwrite addonnomount.txt because it changed during every merge attempt.");
            return false;
        }

        private bool TryLoadDisabledIdsForMergeNoLock(
            out HashSet<string> disabledIds,
            out FileFingerprint fingerprint)
        {
            disabledIds = new HashSet<string>(StringComparer.Ordinal);
            fingerprint = default;
            try
            {
                var read = ReadStableFileNoLock();
                var text = read.Bytes.Length == 0
                    ? string.Empty
                    : Encoding.UTF8.GetString(read.Bytes);
                if (!TryParseNoMountDocument(text, out var parsedIds))
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[GmodAddonStateStore] Refusing to overwrite malformed addonnomount.txt.");
                    return false;
                }

                disabledIds = new HashSet<string>(
                    parsedIds,
                    StringComparer.Ordinal);
                fingerprint = read.Fingerprint;
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[GmodAddonStateStore] Failed to read addonnomount.txt for merge: {ex.Message}");
                return false;
            }
        }

        private bool SaveDisabledIdsNoLock(HashSet<string> disabledIds)
        {
            var temp = noMountFilePath + ".tmp";
            try
            {
                var dir = Path.GetDirectoryName(noMountFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var content = BuildNoMountFileContent(disabledIds);
                File.WriteAllText(temp, content, new UTF8Encoding(false));

                if (File.Exists(noMountFilePath))
                {
                    File.Replace(temp, noMountFilePath, null);
                }
                else
                {
                    File.Move(temp, noMountFilePath);
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GmodAddonStateStore] Failed to save addonnomount.txt: {ex.Message}");
                try
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
                catch (Exception cleanupError)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[GmodAddonStateStore] Failed to clean temporary addonnomount file: {cleanupError.Message}");
                }

                return false;
            }
        }

        private bool ExecuteWithPathMutexNoLock(Func<bool> action)
        {
            try
            {
                using var pathMutex = new Mutex(
                    initiallyOwned: false,
                    name: pathMutexName);
                var acquired = false;
                try
                {
                    try
                    {
                        acquired = pathMutex.WaitOne(PathMutexTimeout);
                    }
                    catch (AbandonedMutexException)
                    {
                        acquired = true;
                    }

                    if (!acquired)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            "[GmodAddonStateStore] Timed out waiting for the path-scoped addonnomount mutex.");
                        return false;
                    }

                    return action();
                }
                finally
                {
                    if (acquired)
                    {
                        pathMutex.ReleaseMutex();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[GmodAddonStateStore] Failed to coordinate addonnomount update: {ex.Message}");
                return false;
            }
        }

        private string BuildNoMountFileContent(IEnumerable<string> disabledIds)
        {
            var sb = new StringBuilder();
            sb.AppendLine("\"addonnomount\"");
            sb.AppendLine("{");

            var sortedIds = NormalizeDisabledIds(disabledIds);
            for (int i = 0; i < sortedIds.Count; i++)
            {
                sb.Append("\t\"").Append(i + 1).Append("\"\t\t\"")
                  .Append(sortedIds[i]).AppendLine("\"");
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        private StableFileRead ReadStableFileNoLock()
        {
            Exception? lastError = null;

            for (var attempt = 0; attempt < StableReadAttempts; attempt++)
            {
                try
                {
                    var before = GetMetadataFingerprint();
                    var bytes = ReadAllBytesShared();
                    DuringStableReadForTesting?.Invoke(attempt);
                    var after = GetMetadataFingerprint();

                    if (before.Equals(after))
                    {
                        return new StableFileRead(
                            bytes,
                            after.LastWriteUtc,
                            after.FileSize,
                            ComputeSha256Hex(bytes));
                    }
                }
                catch (IOException ex)
                {
                    lastError = ex;
                }
                catch (UnauthorizedAccessException ex)
                {
                    lastError = ex;
                }

                Thread.Sleep(StableReadDelayMs);
            }

            throw new IOException(
                "Could not obtain a stable addonnomount.txt snapshot.",
                lastError);
        }

        private byte[] ReadAllBytesShared()
        {
            if (!File.Exists(noMountFilePath))
            {
                return Array.Empty<byte>();
            }

            using var stream = new FileStream(
                noMountFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }

        private FileMetadataFingerprint GetMetadataFingerprint()
        {
            if (!File.Exists(noMountFilePath))
            {
                return new FileMetadataFingerprint(null, null);
            }

            var info = new FileInfo(noMountFilePath);
            return new FileMetadataFingerprint(
                info.LastWriteTimeUtc,
                info.Length);
        }

        private static bool IsNormalizedWorkshopId(string? id)
        {
            return !string.IsNullOrWhiteSpace(id) && ulong.TryParse(id, out _);
        }

        private static string NormalizeWorkshopId(string id)
        {
            return ulong.Parse(id).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string ComputeSha256Hex(byte[] bytes)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(bytes);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        private static string BuildPathMutexName(string path)
        {
            var normalizedPath = Path.GetFullPath(path)
                .Trim()
                .ToUpperInvariant();
            var hash = ComputeSha256Hex(
                Encoding.UTF8.GetBytes(normalizedPath));
            return "GmodAddonManager_AddonState_" + hash;
        }

        private sealed class NoMountDocumentParser
        {
            private readonly string text;
            private int position;

            public NoMountDocumentParser(string text)
            {
                this.text = text;
            }

            public bool IsAtEnd => position >= text.Length;

            public bool TryConsume(char expected)
            {
                if (IsAtEnd || text[position] != expected)
                {
                    return false;
                }

                position++;
                return true;
            }

            public bool TryReadQuotedToken(out string value)
            {
                value = string.Empty;
                if (!TryConsume('"'))
                {
                    return false;
                }

                var builder = new StringBuilder();
                while (!IsAtEnd)
                {
                    var current = text[position++];
                    if (current == '"')
                    {
                        value = builder.ToString();
                        return true;
                    }

                    if (current == '\r' || current == '\n')
                    {
                        return false;
                    }

                    if (current == '\\')
                    {
                        if (IsAtEnd)
                        {
                            return false;
                        }

                        current = text[position++];
                        if (current != '"' && current != '\\')
                        {
                            return false;
                        }
                    }

                    builder.Append(current);
                }

                return false;
            }

            public bool TrySkipTrivia(out bool skippedAny)
            {
                skippedAny = false;
                while (!IsAtEnd)
                {
                    if (char.IsWhiteSpace(text[position]) || text[position] == '\uFEFF')
                    {
                        skippedAny = true;
                        position++;
                        continue;
                    }

                    if (position + 1 >= text.Length || text[position] != '/')
                    {
                        return true;
                    }

                    if (text[position + 1] == '/')
                    {
                        skippedAny = true;
                        position += 2;
                        while (!IsAtEnd && text[position] != '\r' && text[position] != '\n')
                        {
                            position++;
                        }
                        continue;
                    }

                    if (text[position + 1] != '*')
                    {
                        return true;
                    }

                    skippedAny = true;
                    position += 2;
                    var commentClosed = false;
                    while (position + 1 < text.Length)
                    {
                        if (text[position] == '*' && text[position + 1] == '/')
                        {
                            position += 2;
                            commentClosed = true;
                            break;
                        }
                        position++;
                    }

                    if (!commentClosed)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        private readonly struct StableFileRead
        {
            public StableFileRead(
                byte[] bytes,
                DateTime? lastWriteUtc,
                long? fileSize,
                string physicalHash)
            {
                Bytes = bytes;
                LastWriteUtc = lastWriteUtc;
                FileSize = fileSize;
                Fingerprint = new FileFingerprint(
                    lastWriteUtc,
                    fileSize,
                    physicalHash);
            }

            public byte[] Bytes { get; }
            public DateTime? LastWriteUtc { get; }
            public long? FileSize { get; }
            public FileFingerprint Fingerprint { get; }
        }

        private readonly struct FileFingerprint : IEquatable<FileFingerprint>
        {
            public FileFingerprint(
                DateTime? lastWriteUtc,
                long? fileSize,
                string? physicalHash)
            {
                LastWriteUtc = lastWriteUtc;
                FileSize = fileSize;
                PhysicalHash = physicalHash;
            }

            private DateTime? LastWriteUtc { get; }
            private long? FileSize { get; }
            private string? PhysicalHash { get; }

            public bool Equals(FileFingerprint other)
            {
                return LastWriteUtc == other.LastWriteUtc &&
                       FileSize == other.FileSize &&
                       string.Equals(
                           PhysicalHash,
                           other.PhysicalHash,
                           StringComparison.Ordinal);
            }
        }

        private readonly struct FileMetadataFingerprint :
            IEquatable<FileMetadataFingerprint>
        {
            public FileMetadataFingerprint(
                DateTime? lastWriteUtc,
                long? fileSize)
            {
                LastWriteUtc = lastWriteUtc;
                FileSize = fileSize;
            }

            public DateTime? LastWriteUtc { get; }
            public long? FileSize { get; }

            public bool Equals(FileMetadataFingerprint other)
            {
                return LastWriteUtc == other.LastWriteUtc &&
                       FileSize == other.FileSize;
            }
        }
    }
}
