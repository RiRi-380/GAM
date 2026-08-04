using System.IO.Compression;
using System.Text;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class UntrustedInputResourceLimitTests : IDisposable
{
    private readonly string testDirectory = Path.Combine(
        Path.GetTempPath(),
        "gam-untrusted-input-tests-" + Guid.NewGuid().ToString("N"));

    public UntrustedInputResourceLimitTests()
    {
        Directory.CreateDirectory(testDirectory);
    }

    [Fact]
    public async Task SingleAssetFile_OverTechnicalByteLimit_IsRejectedBeforeAllocation()
    {
        var path = Path.Combine(testDirectory, "oversized.gam");
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            stream.SetLength((long)GamAssetDocumentCodec.MaximumDocumentBytes + 1);
        }

        var error = await Assert.ThrowsAsync<GamAssetDocumentException>(() =>
            new GamAssetFileService().ReadAsync(path));

        Assert.Contains("safety limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bundle_CompressedManifestBomb_IsRejectedByDecompressedByteLimit()
    {
        var bytes = CreateArchive(archive =>
        {
            var manifest = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
            using (var stream = manifest.Open())
            {
                var block = new byte[1024 * 1024];
                Array.Fill(block, (byte)' ');
                for (var index = 0;
                     index <= GamAssetBundleCodec.MaximumManifestBytes / block.Length;
                     index++)
                {
                    stream.Write(block, 0, block.Length);
                }
            }

            WriteEntry(archive, "manifest.sha256", Encoding.ASCII.GetBytes(new string('0', 64)));
        });

        using var stream = new MemoryStream(bytes, writable: false);
        var error = Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetBundleCodec.Deserialize(stream));

        Assert.Contains("manifest", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("safety limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bundle_TooManyImageEntries_IsRejectedBeforeImageDecoding()
    {
        var bytes = CreateArchive(archive =>
        {
            WriteEntry(archive, "manifest.json", Encoding.UTF8.GetBytes("{}"));
            WriteEntry(archive, "manifest.sha256", Encoding.ASCII.GetBytes(new string('0', 64)));
            for (var index = 0; index <= GamAssetBundleCodec.MaximumImageCount; index++)
            {
                WriteEntry(archive, $"images/assets/asset-{index}.png", Array.Empty<byte>());
            }
        });

        using var stream = new MemoryStream(bytes, writable: false);
        var error = Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetBundleCodec.Deserialize(stream));

        Assert.Contains("image", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("safety limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bundle_OversizedCentralDirectoryDeclaration_IsRejectedBeforeZipArchiveAllocation()
    {
        var bytes = CreateArchive(archive =>
        {
            WriteEntry(archive, "manifest.json", Encoding.UTF8.GetBytes("{}"));
            WriteEntry(archive, "manifest.sha256", Encoding.ASCII.GetBytes(new string('0', 64)));
        });
        var eocdOffset = FindEndOfCentralDirectory(bytes);
        Assert.True(eocdOffset >= 0);
        var oversizedDirectory = 8 * 1024 * 1024 + 1;
        bytes[eocdOffset + 12] = (byte)oversizedDirectory;
        bytes[eocdOffset + 13] = (byte)(oversizedDirectory >> 8);
        bytes[eocdOffset + 14] = (byte)(oversizedDirectory >> 16);
        bytes[eocdOffset + 15] = (byte)(oversizedDirectory >> 24);

        using var stream = new MemoryStream(bytes, writable: false);
        var error = Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetBundleCodec.Deserialize(stream));

        Assert.Contains("central directory", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("safety limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bundle_PreCanceledImport_StopsBeforeManifestParsing()
    {
        var bundle = new GamAssetBundleDocument(
            new[]
            {
                new GamAssetBundleAsset(
                    "asset",
                    "Asset",
                    GamAssetDocumentState.Enabled,
                    GamAssetDocumentMembership.Fixed(new[] { "123" }))
            },
            Array.Empty<GamAssetBundleGroup>());
        using var stream = new MemoryStream();
        GamAssetBundleCodec.Serialize(stream, bundle);
        stream.Position = 0;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            GamAssetBundleCodec.Deserialize(stream, cancellation.Token));
    }

    [Fact]
    public void AddonJsonFile_OverOneMiB_IsRejectedBeforeTextAllocation()
    {
        var path = Path.Combine(testDirectory, "addon.json");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            stream.SetLength((long)AddonJsonReader.MaximumAddonJsonBytes + 1);
        }

        Assert.False(AddonJsonReader.TryReadClassificationDocumentFromFile(
            path,
            out var type,
            out var tags));
        Assert.Null(type);
        Assert.Null(tags);
    }

    [Fact]
    public void Gma_AddonJsonOverOneMiB_IsNotRead()
    {
        var path = Path.Combine(testDirectory, "oversized-addon-json.gma");
        WriteCountedGma(
            path,
            new[]
            {
                new GmaTestEntry(
                    "addon.json",
                    (long)AddonJsonReader.MaximumAddonJsonBytes + 1)
            });

        Assert.False(AddonJsonReader.TryReadFromGma(path, out var type, out var tags));
        Assert.Null(type);
        Assert.Null(tags);
    }

    [Fact]
    public void Gma_BoundedAddonJson_StillParsesNormally()
    {
        var path = Path.Combine(testDirectory, "valid-addon-json.gma");
        var json = Encoding.UTF8.GetBytes(
            "{\"type\":\"Weapon\",\"tags\":[\"fun\",\"build\"]}");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            WriteGmaHeader(writer);
            writer.Write(1U);
            WriteNullTerminated(writer, "addon.json");
            writer.Write((ulong)json.Length);
            writer.Write(0U);
            writer.Write(json);
        }

        Assert.True(AddonJsonReader.TryReadFromGma(path, out var type, out var tags));
        Assert.Equal("Weapon", type);
        Assert.Equal(new[] { "fun", "build" }, tags);
    }

    [Fact]
    public void Gma_StandardNumberedTable_UsesClassificationPathFromLaterEntry()
    {
        var path = Path.Combine(testDirectory, "standard-numbered-paths.gma");
        var entries = Enumerable.Range(1, 20)
            .Select(index => new GmaPayloadEntry(
                index == 20
                    ? "lua/weapons/gmod_tool/stools/example.lua"
                    : $"lua/autorun/client/example-{index}.lua",
                new[] { checked((byte)index) }))
            .ToArray();
        WriteNumberedGma(
            path,
            entries);

        Assert.True(AddonJsonReader.TryReadFromGma(path, out var type, out var tags));
        Assert.Equal("Tool", type);
        Assert.Null(tags);
    }

    [Fact]
    public void Gma_StandardNumberedTable_ReadsAddonJsonFromLaterEntry()
    {
        var path = Path.Combine(testDirectory, "standard-numbered-addon-json.gma");
        var json = Encoding.UTF8.GetBytes(
            "{\"type\":\"Weapon\",\"tags\":[\"fun\",\"build\"]}");
        WriteNumberedGma(
            path,
            new[]
            {
                new GmaPayloadEntry(
                    "lua/autorun/client/example.lua",
                    Encoding.UTF8.GetBytes("first payload")),
                new GmaPayloadEntry("addon.json", json)
            });

        Assert.True(AddonJsonReader.TryReadFromGma(path, out var type, out var tags));
        Assert.Equal("Weapon", type);
        Assert.Equal(new[] { "fun", "build" }, tags);
    }

    [Fact]
    public void Gma_StandardVersionThree_SkipsRequiredContentStringList()
    {
        var path = Path.Combine(testDirectory, "standard-required-content.gma");
        WriteNumberedGma(
            path,
            new[]
            {
                new GmaPayloadEntry("sound/example.wav", new byte[] { 1 })
            },
            description: "{\"type\":\"effects\",\"tags\":[\"roleplay\"]}",
            requiredContent: new[] { "base", "other-addon" });

        Assert.True(AddonJsonReader.TryReadFromGma(path, out var type, out var tags));
        Assert.Equal("effects", type);
        Assert.Equal(new[] { "roleplay" }, tags);
    }

    [Fact]
    public void Gma_StandardVersionZero_ReadsHeaderWithoutRequiredContentField()
    {
        var path = Path.Combine(testDirectory, "standard-version-zero.gma");
        WriteNumberedGma(
            path,
            new[]
            {
                new GmaPayloadEntry("sound/example.wav", new byte[] { 1 })
            },
            description: "{\"type\":\"effects\",\"tags\":[\"scenic\"]}",
            formatVersion: 0);

        Assert.True(AddonJsonReader.TryReadFromGma(path, out var type, out var tags));
        Assert.Equal("effects", type);
        Assert.Equal(new[] { "scenic" }, tags);
    }

    [Fact]
    public void Gma_StandardNumberedTable_TruncatedBeforeTerminator_IsRejected()
    {
        var path = Path.Combine(testDirectory, "standard-truncated-table.gma");
        WriteNumberedGma(
            path,
            new[]
            {
                new GmaPayloadEntry(
                    "lua/autorun/client/example.lua",
                    Encoding.ASCII.GetBytes("ABCD")),
                new GmaPayloadEntry(
                    "lua/weapons/gmod_tool/stools/example.lua",
                    new byte[] { 5 })
            },
            includeTableTerminator: false);

        Assert.False(AddonJsonReader.TryReadFromGma(path, out _, out _));
    }

    [Fact]
    public void Gma_StandardNumberedTable_EntryLimit_IsEnforced()
    {
        var path = Path.Combine(testDirectory, "standard-entry-limit.gma");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            WriteGmaHeader(writer);
            for (uint fileNumber = 1;
                 fileNumber <= (uint)AddonJsonReader.MaximumGmaEntryCount + 1U;
                 fileNumber++)
            {
                writer.Write(fileNumber);
                WriteNullTerminated(writer, "a");
                writer.Write(0UL);
                writer.Write(0U);
            }
            writer.Write(0U); // table terminator
            writer.Write(0U); // archive CRC
        }

        Assert.False(AddonJsonReader.TryReadFromGma(path, out _, out _));
    }

    [Fact]
    public void Gma_EntryCountOverTechnicalLimit_IsRejectedWithoutLoopingDeclaredCount()
    {
        var path = Path.Combine(testDirectory, "too-many-entries.gma");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            WriteGmaHeader(writer);
            writer.Write((uint)AddonJsonReader.MaximumGmaEntryCount + 1);
        }

        Assert.False(AddonJsonReader.TryReadFromGma(path, out _, out _));
    }

    [Fact]
    public void Gma_OverlongEntryPath_IsRejectedWithoutParserDesynchronization()
    {
        var path = Path.Combine(testDirectory, "overlong-path.gma");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            WriteGmaHeader(writer);
            writer.Write(1U);
            writer.Write(Encoding.UTF8.GetBytes(
                new string('a', AddonJsonReader.MaximumGmaPathBytes + 1)));
            writer.Write((byte)0);
            writer.Write(0UL);
            writer.Write(0U);
        }

        Assert.False(AddonJsonReader.TryReadFromGma(path, out _, out _));
    }

    [Fact]
    public void Gma_AggregatePathMetadataBudget_IsSharedAcrossFormatFallbacks()
    {
        var path = Path.Combine(testDirectory, "path-metadata-budget.gma");
        var entryPath = new string('a', AddonJsonReader.MaximumGmaPathBytes);
        var bytesPerPath = AddonJsonReader.MaximumGmaPathBytes + 1;
        var entryCount = AddonJsonReader.MaximumGmaPathMetadataBytes / bytesPerPath + 1;
        Assert.InRange(entryCount, 1, AddonJsonReader.MaximumGmaEntryCount);

        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            WriteGmaHeader(writer);
            writer.Write((uint)entryCount);
            for (var index = 0; index < entryCount; index++)
            {
                WriteNullTerminated(writer, entryPath);
                writer.Write(0UL);
                writer.Write(0U);
            }
        }

        Assert.False(AddonJsonReader.TryReadFromGma(path, out _, out _));
    }

    [Fact]
    public void Gma_EntryOffsetsThatExceedPayload_AreRejected()
    {
        var path = Path.Combine(testDirectory, "invalid-offset.gma");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            WriteGmaHeader(writer);
            writer.Write(2U);
            WriteNullTerminated(writer, "first.bin");
            writer.Write(ulong.MaxValue);
            writer.Write(0U);
            WriteNullTerminated(writer, "addon.json");
            writer.Write(1UL);
            writer.Write(0U);
            writer.Write((byte)'{');
        }

        Assert.False(AddonJsonReader.TryReadFromGma(path, out _, out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static byte[] CreateArchive(Action<ZipArchive> write)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            write(archive);
        }

        return output.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    private static int FindEndOfCentralDirectory(byte[] bytes)
    {
        for (var index = bytes.Length - 22; index >= 0; index--)
        {
            if (bytes[index] == 0x50 &&
                bytes[index + 1] == 0x4b &&
                bytes[index + 2] == 0x05 &&
                bytes[index + 3] == 0x06)
            {
                return index;
            }
        }

        return -1;
    }

    private static void WriteCountedGma(string path, IReadOnlyList<GmaTestEntry> entries)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        WriteGmaHeader(writer);
        writer.Write((uint)entries.Count);
        foreach (var entry in entries)
        {
            WriteNullTerminated(writer, entry.Path);
            writer.Write((ulong)entry.Size);
            writer.Write(0U);
        }

        var payloadStart = stream.Position;
        var payloadLength = entries.Sum(entry => entry.Size);
        stream.SetLength(payloadStart + payloadLength);
    }

    private static void WriteNumberedGma(
        string path,
        IReadOnlyList<GmaPayloadEntry> entries,
        string description = "description",
        IReadOnlyList<string>? requiredContent = null,
        byte formatVersion = 3,
        bool includeTableTerminator = true)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        WriteGmaHeader(
            writer,
            description,
            requiredContent,
            formatVersion);

        uint fileNumber = 0;
        foreach (var entry in entries)
        {
            writer.Write(++fileNumber);
            WriteNullTerminated(writer, entry.Path);
            writer.Write((ulong)entry.Data.LongLength);
            writer.Write(0U);
        }

        if (includeTableTerminator)
        {
            writer.Write(0U);
        }

        foreach (var entry in entries)
        {
            writer.Write(entry.Data);
        }
        writer.Write(0U); // archive CRC
    }

    private static void WriteGmaHeader(
        BinaryWriter writer,
        string description = "description",
        IReadOnlyList<string>? requiredContent = null,
        byte formatVersion = 3)
    {
        writer.Write(Encoding.ASCII.GetBytes("GMAD"));
        writer.Write(formatVersion);
        writer.Write(0UL);
        writer.Write(0UL);
        if (formatVersion > 1)
        {
            foreach (var required in requiredContent ?? Array.Empty<string>())
            {
                WriteNullTerminated(writer, required);
            }
            writer.Write((byte)0);
        }
        WriteNullTerminated(writer, "name");
        WriteNullTerminated(writer, description);
        WriteNullTerminated(writer, "author");
        writer.Write(1U);
    }

    private static void WriteNullTerminated(BinaryWriter writer, string value)
    {
        writer.Write(Encoding.UTF8.GetBytes(value));
        writer.Write((byte)0);
    }

    private sealed record GmaTestEntry(string Path, long Size);
    private sealed record GmaPayloadEntry(string Path, byte[] Data);
}
