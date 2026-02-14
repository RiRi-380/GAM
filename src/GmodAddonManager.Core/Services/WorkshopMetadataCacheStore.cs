using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;

namespace GmodAddonManager.Core.Services
{
    internal sealed class WorkshopMetadataCacheStore
    {
        private const string DefaultDbFileName = "workshop.db";
        private const int SnippetLength = 300;
        private static readonly char[] TagSeparator = { '|' };
        private static readonly object InitLock = new object();
        private readonly string _dbPath;
        private bool _initialized;

        internal WorkshopMetadataCacheStore(string? dbPath = null)
        {
            _dbPath = string.IsNullOrWhiteSpace(dbPath)
                ? GetDefaultDbPath()
                : dbPath;
        }

        internal static string GetDefaultDbPath()
        {
            var overridePath = Environment.GetEnvironmentVariable("GAM_METADATA_CACHE_PATH");
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                return overridePath;
            }

            var overrideDir = Environment.GetEnvironmentVariable("GAM_METADATA_CACHE_DIR");
            var baseDir = !string.IsNullOrWhiteSpace(overrideDir)
                ? overrideDir
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "GmodAddonManager",
                    "cache");

            Directory.CreateDirectory(baseDir);
            return Path.Combine(baseDir, DefaultDbFileName);
        }

        internal sealed class CacheEntry
        {
            public WorkshopItemDetails Details { get; set; } = new WorkshopItemDetails();
            public DateTime FetchedAtUtc { get; set; } = DateTime.MinValue;
        }

        internal Dictionary<string, CacheEntry> GetCoreBatch(IReadOnlyList<string> ids)
        {
            var results = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
            if (ids == null || ids.Count == 0)
            {
                return results;
            }

            EnsureInitialized();

            var batches = ChunkIds(ids, 900);
            using var connection = OpenConnection();
            EnsureTempBatchIdsTable(connection);
            foreach (var batch in batches)
            {
                ReplaceTempBatchIds(connection, batch);

                using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    "SELECT id, title, description_snippet, preview_url, timeupdated_web, timecreated_web, creator, file_size, tags, last_fetched_utc " +
                    "FROM published_file_core WHERE id IN (SELECT id FROM temp_batch_ids);";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetString(0);
                    var details = new WorkshopItemDetails
                    {
                        Id = id,
                        Title = reader.IsDBNull(1) ? null : reader.GetString(1),
                        Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                        PreviewUrl = reader.IsDBNull(3) ? null : reader.GetString(3),
                        TimeUpdated = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                        TimeCreated = reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                        Creator = reader.IsDBNull(6) ? null : reader.GetString(6),
                        FileSize = reader.IsDBNull(7) ? 0UL : (ulong)reader.GetInt64(7),
                        Tags = DeserializeTags(reader.IsDBNull(8) ? null : reader.GetString(8))
                    };

                    var fetchedAtUtc = reader.IsDBNull(9)
                        ? DateTime.MinValue
                        : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(9)).UtcDateTime;

                    results[id] = new CacheEntry
                    {
                        Details = details,
                        FetchedAtUtc = fetchedAtUtc
                    };
                }
            }

            return results;
        }

        internal Dictionary<string, DateTime> GetNegativeBatch(IReadOnlyList<string> ids)
        {
            var results = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            if (ids == null || ids.Count == 0)
            {
                return results;
            }

            EnsureInitialized();

            var batches = ChunkIds(ids, 900);
            using var connection = OpenConnection();
            EnsureTempBatchIdsTable(connection);
            foreach (var batch in batches)
            {
                ReplaceTempBatchIds(connection, batch);

                using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    "SELECT id, last_fetched_utc FROM published_file_negative " +
                    "WHERE id IN (SELECT id FROM temp_batch_ids);";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetString(0);
                    if (reader.IsDBNull(1))
                    {
                        continue;
                    }

                    var fetchedAtUtc = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)).UtcDateTime;
                    results[id] = fetchedAtUtc;
                }
            }

            return results;
        }

        internal void UpsertNegative(IEnumerable<string> ids)
        {
            if (ids == null)
            {
                return;
            }

            var list = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
            if (list.Count == 0)
            {
                return;
            }

            EnsureInitialized();

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "INSERT INTO published_file_negative (id, last_fetched_utc) " +
                "VALUES (@id, @fetched) " +
                "ON CONFLICT(id) DO UPDATE SET last_fetched_utc = excluded.last_fetched_utc;";
            cmd.Parameters.Add("@id", SqliteType.Text);
            cmd.Parameters.Add("@fetched", SqliteType.Integer);

            var fetchedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var id in list)
            {
                cmd.Parameters["@id"].Value = id;
                cmd.Parameters["@fetched"].Value = fetchedUnix;
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        internal void DeleteNegative(IEnumerable<string> ids)
        {
            if (ids == null)
            {
                return;
            }

            var list = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
            if (list.Count == 0)
            {
                return;
            }

            EnsureInitialized();

            var batches = ChunkIds(list, 900);
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            EnsureTempBatchIdsTable(connection);
            foreach (var batch in batches)
            {
                ReplaceTempBatchIds(connection, batch, transaction);

                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM published_file_negative WHERE id IN (SELECT id FROM temp_batch_ids);";

                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        internal bool TryGetFullDescription(string id, out string? fullDescription)
        {
            fullDescription = null;
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            EnsureInitialized();

            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT desc_full FROM published_file_desc WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);

            using var reader = cmd.ExecuteReader(CommandBehavior.SingleRow);
            if (!reader.Read() || reader.IsDBNull(0))
            {
                return false;
            }

            var blob = (byte[])reader["desc_full"];
            fullDescription = Decompress(blob);
            return !string.IsNullOrEmpty(fullDescription);
        }

        internal void UpsertBatch(IEnumerable<WorkshopItemDetails> detailsBatch)
        {
            UpsertBatch(detailsBatch, storeFullDescription: true);
        }

        internal void UpsertBatch(IEnumerable<WorkshopItemDetails> detailsBatch, bool storeFullDescription)
        {
            if (detailsBatch == null)
            {
                return;
            }

            var items = detailsBatch.Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id)).ToList();
            if (items.Count == 0)
            {
                return;
            }

            EnsureInitialized();

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            using var coreCmd = connection.CreateCommand();
                coreCmd.CommandText =
                    "INSERT INTO published_file_core " +
                    "(id, title, description_snippet, preview_url, timeupdated_web, timecreated_web, creator, file_size, tags, last_fetched_utc) " +
                    "VALUES (@id, @title, @desc_snip, @preview, @timeupdated, @timecreated, @creator, @filesize, @tags, @fetched) " +
                    "ON CONFLICT(id) DO UPDATE SET " +
                    "title = excluded.title, " +
                    "description_snippet = excluded.description_snippet, " +
                    "preview_url = excluded.preview_url, " +
                    "timeupdated_web = excluded.timeupdated_web, " +
                    "timecreated_web = excluded.timecreated_web, " +
                    "creator = excluded.creator, " +
                    "file_size = excluded.file_size, " +
                    "tags = excluded.tags, " +
                    "last_fetched_utc = excluded.last_fetched_utc;";

            coreCmd.Parameters.Add("@id", SqliteType.Text);
            coreCmd.Parameters.Add("@title", SqliteType.Text);
            coreCmd.Parameters.Add("@desc_snip", SqliteType.Text);
            coreCmd.Parameters.Add("@preview", SqliteType.Text);
            coreCmd.Parameters.Add("@timeupdated", SqliteType.Integer);
            coreCmd.Parameters.Add("@timecreated", SqliteType.Integer);
            coreCmd.Parameters.Add("@creator", SqliteType.Text);
            coreCmd.Parameters.Add("@filesize", SqliteType.Integer);
            coreCmd.Parameters.Add("@tags", SqliteType.Text);
            coreCmd.Parameters.Add("@fetched", SqliteType.Integer);

            SqliteCommand? descCmd = null;
            if (storeFullDescription)
            {
                descCmd = connection.CreateCommand();
                descCmd.CommandText =
                    "INSERT INTO published_file_desc (id, desc_full, last_fetched_utc) " +
                    "VALUES (@id, @desc_full, @fetched) " +
                    "ON CONFLICT(id) DO UPDATE SET " +
                    "desc_full = excluded.desc_full, " +
                    "last_fetched_utc = excluded.last_fetched_utc;";

                descCmd.Parameters.Add("@id", SqliteType.Text);
                descCmd.Parameters.Add("@desc_full", SqliteType.Blob);
                descCmd.Parameters.Add("@fetched", SqliteType.Integer);
            }

            var fetchedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var item in items)
            {
                var snippet = CreateSnippet(item.Description, SnippetLength);

                coreCmd.Parameters["@id"].Value = item.Id!;
                coreCmd.Parameters["@title"].Value = (object?)item.Title ?? DBNull.Value;
                coreCmd.Parameters["@desc_snip"].Value = (object?)snippet ?? DBNull.Value;
                coreCmd.Parameters["@preview"].Value = (object?)item.PreviewUrl ?? DBNull.Value;
                coreCmd.Parameters["@timeupdated"].Value = item.TimeUpdated;
                coreCmd.Parameters["@timecreated"].Value = item.TimeCreated;
                coreCmd.Parameters["@creator"].Value = (object?)item.Creator ?? DBNull.Value;
                coreCmd.Parameters["@filesize"].Value = (long)item.FileSize;
                coreCmd.Parameters["@tags"].Value = (object?)SerializeTags(item.Tags) ?? DBNull.Value;
                coreCmd.Parameters["@fetched"].Value = fetchedUnix;
                coreCmd.ExecuteNonQuery();

                if (storeFullDescription && descCmd != null && !string.IsNullOrWhiteSpace(item.Description))
                {
                    var compressed = Compress(item.Description);
                    descCmd.Parameters["@id"].Value = item.Id!;
                    descCmd.Parameters["@desc_full"].Value = compressed;
                    descCmd.Parameters["@fetched"].Value = fetchedUnix;
                    descCmd.ExecuteNonQuery();
                }
            }

            transaction.Commit();

            DeleteNegative(items.Select(item => item.Id!));
        }

        private void EnsureInitialized()
        {
            if (_initialized)
            {
                return;
            }

            lock (InitLock)
            {
                if (_initialized)
                {
                    return;
                }

                var dir = Path.GetDirectoryName(_dbPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                using var connection = OpenConnection();
                using var pragma = connection.CreateCommand();
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY;";
                pragma.ExecuteNonQuery();

                using var schema = connection.CreateCommand();
                schema.CommandText =
                    "CREATE TABLE IF NOT EXISTS published_file_core (" +
                    "id TEXT PRIMARY KEY," +
                    "title TEXT," +
                    "description_snippet TEXT," +
                    "preview_url TEXT," +
                    "timeupdated_web INTEGER," +
                    "timecreated_web INTEGER," +
                    "creator TEXT," +
                    "file_size INTEGER," +
                    "tags TEXT," +
                    "last_fetched_utc INTEGER" +
                    ");" +
                    "CREATE TABLE IF NOT EXISTS published_file_negative (" +
                    "id TEXT PRIMARY KEY," +
                    "last_fetched_utc INTEGER" +
                    ");" +
                    "CREATE TABLE IF NOT EXISTS published_file_desc (" +
                    "id TEXT PRIMARY KEY," +
                    "desc_full BLOB," +
                    "last_fetched_utc INTEGER" +
                    ");" +
                    "CREATE INDEX IF NOT EXISTS idx_published_file_core_fetched " +
                    "ON published_file_core(last_fetched_utc);" +
                    "CREATE INDEX IF NOT EXISTS idx_published_file_negative_fetched " +
                    "ON published_file_negative(last_fetched_utc);";
                schema.ExecuteNonQuery();

                EnsureTagsColumnExists(connection);

                _initialized = true;
            }
        }

        private static void EnsureTagsColumnExists(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(published_file_core);";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (string.Equals(name, "tags", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE published_file_core ADD COLUMN tags TEXT;";
            alter.ExecuteNonQuery();
        }

        private static void EnsureTempBatchIdsTable(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "CREATE TEMP TABLE IF NOT EXISTS temp_batch_ids (" +
                "id TEXT PRIMARY KEY" +
                ");";
            cmd.ExecuteNonQuery();
        }

        private static void ReplaceTempBatchIds(
            SqliteConnection connection,
            IReadOnlyList<string> ids,
            SqliteTransaction? transaction = null)
        {
            using var clearCmd = connection.CreateCommand();
            clearCmd.Transaction = transaction;
            clearCmd.CommandText = "DELETE FROM temp_batch_ids;";
            clearCmd.ExecuteNonQuery();

            using var insertCmd = connection.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = "INSERT INTO temp_batch_ids (id) VALUES (@id);";
            insertCmd.Parameters.Add("@id", SqliteType.Text);

            foreach (var id in ids)
            {
                insertCmd.Parameters["@id"].Value = id;
                insertCmd.ExecuteNonQuery();
            }
        }

        private SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection($"Data Source={_dbPath};Cache=Shared");
            connection.Open();
            return connection;
        }

        private static List<List<string>> ChunkIds(IReadOnlyList<string> ids, int chunkSize)
        {
            var batches = new List<List<string>>();
            for (var i = 0; i < ids.Count; i += chunkSize)
            {
                batches.Add(ids.Skip(i).Take(chunkSize).ToList());
            }

            return batches;
        }

        private static string? CreateSnippet(string? description, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(description))
            {
                return null;
            }

            var normalized = description.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            if (normalized.Length <= maxChars)
            {
                return normalized;
            }

            return normalized.Substring(0, maxChars);
        }

        private static string? SerializeTags(string[]? tags)
        {
            if (tags == null || tags.Length == 0)
            {
                return null;
            }

            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            foreach (var tag in tags)
            {
                if (string.IsNullOrWhiteSpace(tag))
                {
                    continue;
                }

                var trimmed = tag.Trim();
                if (unique.Add(trimmed))
                {
                    list.Add(trimmed);
                }
            }

            return list.Count == 0 ? null : string.Join("|", list);
        }

        private static string[]? DeserializeTags(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var parts = raw.Split(TagSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToArray();

            return parts.Length == 0 ? null : parts;
        }

        private static byte[] Compress(string text)
        {
            var input = Encoding.UTF8.GetBytes(text);
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write(input, 0, input.Length);
            }

            return output.ToArray();
        }

        private static string Decompress(byte[] data)
        {
            using var input = new MemoryStream(data);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return Encoding.UTF8.GetString(output.ToArray());
        }
    }
}
