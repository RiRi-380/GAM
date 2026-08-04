using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace GmodAddonManager.Core.Services
{
    public static class AddonJsonReader
    {
        private const int MaxStringBytes = 4096;

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
                var json = File.ReadAllText(jsonPath);
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

                reader.ReadByte(); // version
                reader.ReadUInt64(); // steam id
                reader.ReadUInt64(); // timestamp
                var requiredCount = reader.ReadByte();
                for (var i = 0; i < requiredCount; i++)
                {
                    if (ReadNullTerminatedString(reader) == null)
                    {
                        return false;
                    }
                }

                if (ReadNullTerminatedString(reader) == null) return false; // name
                if (ReadNullTerminatedString(reader) == null) return false; // description
                if (ReadNullTerminatedString(reader) == null) return false; // author

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

                if (!TryReadEntriesWithCount(reader, stream.Length, size64: true, out entries, out dataStart))
                {
                    reader.BaseStream.Position = entriesStart;
                    if (!TryReadEntriesWithCount(reader, stream.Length, size64: false, out entries, out dataStart))
                    {
                        reader.BaseStream.Position = entriesStart;
                        if (!TryReadEntries(reader, stream.Length, size64: true, out entries, out dataStart))
                        {
                            reader.BaseStream.Position = entriesStart;
                            if (!TryReadEntries(reader, stream.Length, size64: false, out entries, out dataStart))
                            {
                                return false;
                            }
                        }
                    }
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
                        offset += entry.Size;
                    }

                    if (addonJsonEntry.Size > 0 &&
                        addonJsonEntry.Size <= int.MaxValue &&
                        offset >= 0 &&
                        offset + addonJsonEntry.Size <= stream.Length)
                    {
                        stream.Position = offset;
                        var bytes = reader.ReadBytes((int)addonJsonEntry.Size);
                        var json = Encoding.UTF8.GetString(bytes).Trim('\uFEFF', '\u0000', '\u001A');
                        classificationDocumentRead = TryParseAddonJson(
                            json,
                            out type,
                            out tags);
                    }
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
                var obj = JObject.Parse(json);
                var typeValue = obj.Value<string>("type");
                if (!string.IsNullOrWhiteSpace(typeValue))
                {
                    type = typeValue.Trim();
                }

                var tagsToken = obj["tags"];
                if (tagsToken is JArray tagsArray)
                {
                    tags = tagsArray
                        .Select(t => t?.ToString())
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .Select(t => t!.Trim())
                        .ToArray();
                }
                else if (tagsToken is JValue tagsValue)
                {
                    var raw = tagsValue.ToString();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        tags = SplitTags(raw).ToArray();
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

        private static string? ReadNullTerminatedString(BinaryReader reader)
        {
            var bytes = new List<byte>();
            try
            {
                byte b;
                while ((b = reader.ReadByte()) != 0)
                {
                    bytes.Add(b);
                    if (bytes.Count >= MaxStringBytes)
                    {
                        break;
                    }
                }
            }
            catch (EndOfStreamException)
            {
                return null;
            }

            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        private static bool TryReadEntriesWithCount(BinaryReader reader, long fileLength, bool size64, out List<GmaEntry> entries, out long dataStart)
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
                if (fileCount == 0)
                {
                    return false;
                }

                for (var i = 0; i < fileCount; i++)
                {
                    var path = ReadNullTerminatedString(reader);
                    if (string.IsNullOrEmpty(path))
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

                var totalSize = entries.Sum(e => e.Size);
                if (dataStart + totalSize > fileLength)
                {
                    var peek = reader.BaseStream.Position < fileLength
                        ? reader.ReadByte()
                        : -1;
                    if (peek == 0)
                    {
                        dataStart = reader.BaseStream.Position;
                        if (dataStart + totalSize > fileLength)
                        {
                            return false;
                        }
                    }
                    else
                    {
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadEntries(BinaryReader reader, long fileLength, bool size64, out List<GmaEntry> entries, out long dataStart)
        {
            entries = new List<GmaEntry>();
            dataStart = 0;

            try
            {
                while (true)
                {
                    var path = ReadNullTerminatedString(reader);
                    if (path == null)
                    {
                        return false;
                    }

                    if (path.Length == 0)
                    {
                        break;
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

                long totalSize = 0;
                foreach (var entry in entries)
                {
                    checked
                    {
                        totalSize += entry.Size;
                    }
                }

                if (dataStart + totalSize > fileLength)
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
    }
}
