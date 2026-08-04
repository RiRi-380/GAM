using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GmodAddonManager.Core.Services
{
    public static class AddonJsonReader
    {
        public const int MaximumAddonJsonBytes = 1024 * 1024;
        public const int MaximumGmaEntryCount = 100_000;
        public const int MaximumGmaPathBytes = 4096;
        public const int MaximumGmaPathMetadataBytes = 16 * 1024 * 1024;

        private const byte MaximumSupportedGmaVersion = 3;
        private const int MaximumHeaderStringBytes = 4096;
        private const int MaximumRequiredContentCount = 1024;
        private const int MaximumTagCount = 1024;
        private const int MaximumMetadataValueLength = 512;
        private const int MaximumJsonDepth = 32;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

        public static bool TryReadFromFile(string jsonPath, out string? type, out string[]? tags)
        {
            var parsed = TryReadClassificationDocumentFromFile(
                jsonPath,
                out type,
                out tags);
            return parsed &&
                   (!string.IsNullOrWhiteSpace(type) ||
                    (tags != null && tags.Length > 0));
        }

        public static bool TryReadClassificationDocumentFromFile(
            string jsonPath,
            out string? type,
            out string[]? tags)
        {
            type = null;
            tags = null;

            if (string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath))
            {
                return false;
            }

            try
            {
                if (!TryReadBoundedUtf8File(jsonPath, out var json))
                {
                    return false;
                }

                return TryParseAddonJson(json, out type, out tags);
            }
            catch
            {
                return false;
            }
        }

        public static bool TryReadFromGma(string gmaPath, out string? type, out string[]? tags)
        {
            type = null;
            tags = null;

            if (string.IsNullOrWhiteSpace(gmaPath) || !File.Exists(gmaPath))
            {
                return false;
            }

            try
            {
                using var stream = new FileStream(gmaPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

                if (stream.Length < 22)
                {
                    return false;
                }

                var magicBytes = reader.ReadBytes(4);
                var magic = Encoding.ASCII.GetString(magicBytes);
                if (!string.Equals(magic, "GMAD", StringComparison.Ordinal))
                {
                    return false;
                }

                var formatVersion = reader.ReadByte();
                if (formatVersion > MaximumSupportedGmaVersion)
                {
                    return false;
                }

                reader.ReadUInt64(); // steam id
                reader.ReadUInt64(); // timestamp

                // Official GMAD versions 2 and 3 store required content as a
                // sequence of NUL-terminated strings followed by an empty
                // string. Versions 0 and 1 proceed directly to the addon name.
                if (formatVersion > 1)
                {
                    var requiredContentCount = 0;
                    while (true)
                    {
                        var requiredContent = ReadNullTerminatedString(
                            reader,
                            MaximumHeaderStringBytes);
                        if (requiredContent == null)
                        {
                            return false;
                        }

                        if (requiredContent.Length == 0)
                        {
                            break;
                        }

                        requiredContentCount++;
                        if (requiredContentCount > MaximumRequiredContentCount)
                        {
                            return false;
                        }
                    }
                }

                if (ReadNullTerminatedString(reader, MaximumHeaderStringBytes) == null) return false; // name
                var headerDescription = ReadNullTerminatedString(
                    reader,
                    MaximumHeaderStringBytes);
                if (headerDescription == null) return false;
                if (ReadNullTerminatedString(reader, MaximumHeaderStringBytes) == null) return false; // author

                try
                {
                    reader.ReadUInt32(); // addon version
                }
                catch
                {
                    return false;
                }

                var entriesStart = reader.BaseStream.Position;
                var entries = new List<GmaEntry>();
                long dataStart;
                // Official GMAD uses a numbered file table (1..N, followed by
                // 0). Older GAM builds also accepted two counted and two
                // unnumbered historical layouts. Inspect the first table word
                // before selecting a parser so the official leading file number
                // is never mistaken for a total entry count.
                var pathMetadataBudget = new GmaPathMetadataBudget();
                if (!TryReadEntryTable(
                        reader,
                        entriesStart,
                        stream.Length,
                        pathMetadataBudget,
                        out entries,
                        out dataStart))
                {
                    return false;
                }

                var classificationDocumentRead = false;
                var addonJsonEntry = entries.FirstOrDefault(e => IsAddonJsonPath(e.Path));
                if (addonJsonEntry.Path != null)
                {
                    var offset = dataStart;
                    foreach (var entry in entries)
                    {
                        if (entry.Path == addonJsonEntry.Path)
                        {
                            break;
                        }
                        if (entry.Size < 0 || offset > stream.Length - entry.Size)
                        {
                            return false;
                        }

                        offset += entry.Size;
                    }

                    if (addonJsonEntry.Size > 0 &&
                        addonJsonEntry.Size <= MaximumAddonJsonBytes &&
                        offset >= 0 &&
                        offset <= stream.Length - addonJsonEntry.Size)
                    {
                        stream.Position = offset;
                        var bytes = reader.ReadBytes((int)addonJsonEntry.Size);
                        if (bytes.Length != addonJsonEntry.Size)
                        {
                            return false;
                        }

                        var json = StrictUtf8.GetString(bytes).Trim('\uFEFF', '\u0000', '\u001A');
                        classificationDocumentRead = TryParseAddonJson(
                            json,
                            out type,
                            out tags);
                    }
                }

                // gmad normally consumes addon.json when creating the archive
                // and stores its classification JSON in the header description.
                // An explicitly embedded addon.json remains the higher-priority
                // source for backwards compatibility.
                if (!classificationDocumentRead &&
                    TryParseAddonJson(
                        headerDescription,
                        out var headerType,
                        out var headerTags))
                {
                    type = headerType;
                    tags = headerTags;
                    classificationDocumentRead = true;
                }

                if (string.IsNullOrWhiteSpace(type))
                {
                    type = InferTypeFromPaths(entries.Select(e => e.Path));
                }

                return classificationDocumentRead ||
                       !string.IsNullOrWhiteSpace(type) ||
                       (tags != null && tags.Length > 0);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryParseAddonJson(string json, out string? type, out string[]? tags)
        {
            type = null;
            tags = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            try
            {
                using var stringReader = new StringReader(json);
                using var jsonReader = new JsonTextReader(stringReader)
                {
                    DateParseHandling = DateParseHandling.None,
                    FloatParseHandling = FloatParseHandling.Decimal,
                    MaxDepth = MaximumJsonDepth
                };
                var obj = JObject.Load(
                    jsonReader,
                    new JsonLoadSettings
                    {
                        CommentHandling = CommentHandling.Ignore,
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                    });
                if (jsonReader.Read())
                {
                    return false;
                }

                var typeValue = obj.Value<string>("type");
                if (!string.IsNullOrWhiteSpace(typeValue))
                {
                    type = typeValue.Trim();
                    if (type.Length > MaximumMetadataValueLength)
                    {
                        return false;
                    }
                }

                var tagsToken = obj["tags"];
                if (tagsToken is JArray tagsArray)
                {
                    if (tagsArray.Count > MaximumTagCount)
                    {
                        return false;
                    }

                    var parsedTags = new List<string>(tagsArray.Count);
                    foreach (var token in tagsArray)
                    {
                        if (token == null || token.Type != JTokenType.String)
                        {
                            continue;
                        }

                        var tag = token.Value<string>()?.Trim();
                        if (string.IsNullOrWhiteSpace(tag))
                        {
                            continue;
                        }

                        if (tag.Length > MaximumMetadataValueLength)
                        {
                            return false;
                        }

                        parsedTags.Add(tag);
                    }

                    tags = parsedTags.ToArray();
                }
                else if (tagsToken is JValue tagsValue)
                {
                    var raw = tagsValue.ToString();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        if (raw.Length > MaximumAddonJsonBytes)
                        {
                            return false;
                        }

                        var parsedTags = SplitTags(raw).ToArray();
                        if (parsedTags.Length > MaximumTagCount ||
                            parsedTags.Any(tag => tag.Length > MaximumMetadataValueLength))
                        {
                            return false;
                        }

                        tags = parsedTags;
                    }
                }
            }
            catch
            {
                return false;
            }

            // A syntactically valid addon.json is authoritative even when it
            // explicitly provides no Type or Tags. Smart Assets need to
            // distinguish that confirmed empty classification from I/O failure.
            return true;
        }

        private static IEnumerable<string> SplitTags(string raw)
        {
            var separators = (raw.Contains(',') || raw.Contains(';'))
                ? new[] { ',', ';' }
                : new[] { ' ', '\t', '\r', '\n' };

            foreach (var part in raw.Split(separators, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = part.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    yield return trimmed;
                }
            }
        }

        private static bool TryReadBoundedUtf8File(string path, out string text)
        {
            text = string.Empty;
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < 0 || stream.Length > MaximumAddonJsonBytes)
            {
                return false;
            }

            var bytes = new byte[checked((int)stream.Length)];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0)
                {
                    return false;
                }

                offset += read;
            }

            if (stream.ReadByte() != -1)
            {
                return false;
            }

            text = StrictUtf8.GetString(bytes).TrimStart('\uFEFF');
            return true;
        }

        private static string? ReadNullTerminatedString(
            BinaryReader reader,
            int maximumBytes,
            GmaPathMetadataBudget? pathMetadataBudget = null)
        {
            var bytes = new List<byte>();
            try
            {
                byte b;
                while ((b = reader.ReadByte()) != 0)
                {
                    if (pathMetadataBudget != null &&
                        !pathMetadataBudget.TryConsumeByte())
                    {
                        return null;
                    }

                    if (bytes.Count >= maximumBytes)
                    {
                        return null;
                    }

                    bytes.Add(b);
                }

                if (pathMetadataBudget != null &&
                    !pathMetadataBudget.TryConsumeByte())
                {
                    return null;
                }
            }
            catch (Exception ex) when (
                ex is EndOfStreamException || ex is DecoderFallbackException)
            {
                return null;
            }

            try
            {
                return StrictUtf8.GetString(bytes.ToArray());
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
        }

        private static bool TryReadEntryTable(
            BinaryReader reader,
            long entriesStart,
            long fileLength,
            GmaPathMetadataBudget pathMetadataBudget,
            out List<GmaEntry> entries,
            out long dataStart)
        {
            entries = new List<GmaEntry>();
            dataStart = 0;

            if (entriesStart < 0 || entriesStart > fileLength - sizeof(uint))
            {
                return false;
            }

            reader.BaseStream.Position = entriesStart;
            uint firstTableWord;
            try
            {
                firstTableWord = reader.ReadUInt32();
            }
            catch (EndOfStreamException)
            {
                return false;
            }

            reader.BaseStream.Position = entriesStart;
            if (firstTableWord == 1)
            {
                // A one-entry counted table and every official numbered table
                // both begin with 1. The official form is authoritative: only
                // fall back to the historical counted form after the complete
                // numbered table (sequence, terminator, payload and archive
                // CRC) fails validation.
                if (TryReadNumberedEntries(
                        reader,
                        fileLength,
                        pathMetadataBudget,
                        out entries,
                        out dataStart))
                {
                    return true;
                }

                return TryReadCountedEntryTableWithFallback(
                    reader,
                    entriesStart,
                    fileLength,
                    pathMetadataBudget,
                    out entries,
                    out dataStart);
            }

            if (firstTableWord > 1 && firstTableWord <= MaximumGmaEntryCount)
            {
                return TryReadCountedEntryTableWithFallback(
                    reader,
                    entriesStart,
                    fileLength,
                    pathMetadataBudget,
                    out entries,
                    out dataStart);
            }

            if (firstTableWord == 0)
            {
                return false;
            }

            return TryReadUnnumberedEntryTableWithFallback(
                reader,
                entriesStart,
                fileLength,
                pathMetadataBudget,
                out entries,
                out dataStart);
        }

        private static bool TryReadNumberedEntries(
            BinaryReader reader,
            long fileLength,
            GmaPathMetadataBudget pathMetadataBudget,
            out List<GmaEntry> entries,
            out long dataStart)
        {
            entries = new List<GmaEntry>();
            dataStart = 0;

            try
            {
                uint expectedFileNumber = 1;
                while (true)
                {
                    if (reader.BaseStream.Position > fileLength - sizeof(uint))
                    {
                        return false;
                    }

                    var fileNumber = reader.ReadUInt32();
                    if (fileNumber == 0)
                    {
                        if (entries.Count == 0)
                        {
                            return false;
                        }

                        dataStart = reader.BaseStream.Position;
                        var totalSize = SumEntrySizes(entries, fileLength);
                        return totalSize >= 0 &&
                               HasExactStandardPayloadExtent(
                                   dataStart,
                                   totalSize,
                                   fileLength);
                    }

                    if (entries.Count >= MaximumGmaEntryCount ||
                        fileNumber != expectedFileNumber)
                    {
                        return false;
                    }

                    var path = ReadNullTerminatedString(
                        reader,
                        MaximumGmaPathBytes,
                        pathMetadataBudget);
                    if (!IsSafeGmaPath(path))
                    {
                        return false;
                    }

                    var rawSize = reader.ReadUInt64();
                    reader.ReadUInt32(); // crc
                    if (rawSize > long.MaxValue)
                    {
                        return false;
                    }

                    entries.Add(new GmaEntry(path!, (long)rawSize));
                    expectedFileNumber++;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadCountedEntryTableWithFallback(
            BinaryReader reader,
            long entriesStart,
            long fileLength,
            GmaPathMetadataBudget pathMetadataBudget,
            out List<GmaEntry> entries,
            out long dataStart)
        {
            reader.BaseStream.Position = entriesStart;
            if (TryReadEntriesWithCount(
                    reader,
                    fileLength,
                    size64: true,
                    pathMetadataBudget,
                    out entries,
                    out dataStart))
            {
                return true;
            }

            reader.BaseStream.Position = entriesStart;
            return TryReadEntriesWithCount(
                reader,
                fileLength,
                size64: false,
                pathMetadataBudget,
                out entries,
                out dataStart);
        }

        private static bool TryReadUnnumberedEntryTableWithFallback(
            BinaryReader reader,
            long entriesStart,
            long fileLength,
            GmaPathMetadataBudget pathMetadataBudget,
            out List<GmaEntry> entries,
            out long dataStart)
        {
            reader.BaseStream.Position = entriesStart;
            if (TryReadEntries(
                    reader,
                    fileLength,
                    size64: true,
                    pathMetadataBudget,
                    out entries,
                    out dataStart))
            {
                return true;
            }

            reader.BaseStream.Position = entriesStart;
            return TryReadEntries(
                reader,
                fileLength,
                size64: false,
                pathMetadataBudget,
                out entries,
                out dataStart);
        }

        private static bool TryReadEntriesWithCount(
            BinaryReader reader,
            long fileLength,
            bool size64,
            GmaPathMetadataBudget pathMetadataBudget,
            out List<GmaEntry> entries,
            out long dataStart)
        {
            entries = new List<GmaEntry>();
            dataStart = 0;

            try
            {
                if (reader.BaseStream.Position + sizeof(uint) > fileLength)
                {
                    return false;
                }

                var fileCount = reader.ReadUInt32();
                if (fileCount == 0 || fileCount > MaximumGmaEntryCount)
                {
                    return false;
                }

                var minimumEntryBytes = 1L + (size64 ? sizeof(ulong) : sizeof(uint)) + sizeof(uint);
                if ((long)fileCount * minimumEntryBytes > fileLength - reader.BaseStream.Position)
                {
                    return false;
                }

                for (var i = 0; i < fileCount; i++)
                {
                    var path = ReadNullTerminatedString(
                        reader,
                        MaximumGmaPathBytes,
                        pathMetadataBudget);
                    if (!IsSafeGmaPath(path))
                    {
                        return false;
                    }

                    ulong size = size64 ? reader.ReadUInt64() : reader.ReadUInt32();
                    reader.ReadUInt32(); // crc

                    if (size > long.MaxValue)
                    {
                        return false;
                    }

                    entries.Add(new GmaEntry(path!, (long)size));
                }

                dataStart = reader.BaseStream.Position;

                if (dataStart < 0 || dataStart > fileLength)
                {
                    return false;
                }

                var totalSize = SumEntrySizes(entries, fileLength);
                return totalSize >= 0 &&
                       HasExactPayloadExtent(dataStart, totalSize, fileLength);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadEntries(
            BinaryReader reader,
            long fileLength,
            bool size64,
            GmaPathMetadataBudget pathMetadataBudget,
            out List<GmaEntry> entries,
            out long dataStart)
        {
            entries = new List<GmaEntry>();
            dataStart = 0;

            try
            {
                while (true)
                {
                    var path = ReadNullTerminatedString(
                        reader,
                        MaximumGmaPathBytes,
                        pathMetadataBudget);
                    if (path == null)
                    {
                        return false;
                    }

                    if (path.Length == 0)
                    {
                        break;
                    }

                    if (entries.Count >= MaximumGmaEntryCount || !IsSafeGmaPath(path))
                    {
                        return false;
                    }

                    ulong size = size64 ? reader.ReadUInt64() : reader.ReadUInt32();
                    reader.ReadUInt32(); // crc

                    if (size > long.MaxValue)
                    {
                        return false;
                    }

                    entries.Add(new GmaEntry(path, (long)size));
                }

                dataStart = reader.BaseStream.Position;
                if (dataStart < 0 || dataStart > fileLength)
                {
                    return false;
                }

                var totalSize = SumEntrySizes(entries, fileLength);
                return totalSize >= 0 &&
                       HasExactPayloadExtent(dataStart, totalSize, fileLength);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsAddonJsonPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var normalized = path.Replace('\\', '/');
            return normalized.EndsWith("/addon.json", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "addon.json", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSafeGmaPath(string? path)
        {
            if (string.IsNullOrEmpty(path) || path.Any(char.IsControl))
            {
                return false;
            }

            var normalized = path.Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal) ||
                normalized.IndexOf(':') >= 0)
            {
                return false;
            }

            return normalized.Split('/').All(segment =>
                segment.Length > 0 &&
                !string.Equals(segment, ".", StringComparison.Ordinal) &&
                !string.Equals(segment, "..", StringComparison.Ordinal));
        }

        private static long SumEntrySizes(IEnumerable<GmaEntry> entries, long fileLength)
        {
            long total = 0;
            foreach (var entry in entries)
            {
                if (entry.Size < 0 || entry.Size > fileLength - total)
                {
                    return -1;
                }

                total += entry.Size;
            }

            return total;
        }

        private static bool HasExactStandardPayloadExtent(
            long dataStart,
            long payloadBytes,
            long fileLength)
        {
            if (dataStart < 0 || payloadBytes < 0 || dataStart > fileLength)
            {
                return false;
            }

            var remaining = fileLength - dataStart;
            return remaining >= sizeof(uint) &&
                   payloadBytes == remaining - sizeof(uint);
        }

        private static bool HasExactPayloadExtent(
            long dataStart,
            long payloadBytes,
            long fileLength)
        {
            if (dataStart < 0 || payloadBytes < 0 || dataStart > fileLength)
            {
                return false;
            }

            var remaining = fileLength - dataStart;
            return payloadBytes == remaining ||
                   (remaining >= sizeof(uint) &&
                    payloadBytes == remaining - sizeof(uint));
        }

        private static string? InferTypeFromPaths(IEnumerable<string> paths)
        {
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var normalized = path.Replace('\\', '/').TrimStart('/');
                if (normalized.StartsWith("gamemodes/", StringComparison.OrdinalIgnoreCase))
                {
                    return "Gamemode";
                }

                if (normalized.StartsWith("maps/", StringComparison.OrdinalIgnoreCase) &&
                    normalized.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase))
                {
                    return "Map";
                }

                if (normalized.StartsWith("lua/weapons/", StringComparison.OrdinalIgnoreCase))
                {
                    if (normalized.Contains("gmod_tool", StringComparison.OrdinalIgnoreCase) ||
                        normalized.Contains("stools", StringComparison.OrdinalIgnoreCase))
                    {
                        return "Tool";
                    }
                    return "Weapon";
                }

                if (normalized.StartsWith("lua/vehicles/", StringComparison.OrdinalIgnoreCase))
                {
                    return "Vehicle";
                }

                if (normalized.StartsWith("lua/npc/", StringComparison.OrdinalIgnoreCase))
                {
                    return "NPC";
                }

                if (normalized.StartsWith("lua/tools/", StringComparison.OrdinalIgnoreCase))
                {
                    return "Tool";
                }

                if (normalized.StartsWith("lua/entities/", StringComparison.OrdinalIgnoreCase))
                {
                    return "Entity";
                }

                if (normalized.StartsWith("lua/effects/", StringComparison.OrdinalIgnoreCase))
                {
                    return "Effects";
                }

                if (normalized.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
                {
                    return "Model";
                }
            }

            return null;
        }

        private readonly struct GmaEntry
        {
            public GmaEntry(string path, long size)
            {
                Path = path;
                Size = size;
            }

            public string Path { get; }
            public long Size { get; }
        }

        private sealed class GmaPathMetadataBudget
        {
            private int bytesConsumed;

            public bool TryConsumeByte()
            {
                if (bytesConsumed >= MaximumGmaPathMetadataBytes)
                {
                    return false;
                }

                bytesConsumed++;
                return true;
            }
        }
    }
}
