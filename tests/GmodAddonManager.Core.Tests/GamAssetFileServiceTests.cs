using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class GamAssetFileServiceTests : IDisposable
{
    private readonly string testDirectory = Path.Combine(
        Path.GetTempPath(),
        "gam-document-tests-" + Guid.NewGuid().ToString("N"));

    public GamAssetFileServiceTests()
    {
        Directory.CreateDirectory(testDirectory);
    }

    [Fact]
    public async Task WriteThenRead_RoundTripsUsingGamExtension()
    {
        var path = Path.Combine(testDirectory, "asset.gam");
        var service = new GamAssetFileService();
        var document = CreateDocument("First", "123");

        await service.WriteAsync(path, document);
        var restored = await service.ReadAsync(path);

        Assert.Equal("First", restored.Name);
        Assert.Equal(new[] { "123" }, restored.Membership.AddonIds);
        Assert.Empty(Directory.EnumerateFiles(testDirectory, "*.tmp"));
    }

    [Fact]
    public async Task ReadAny_PreservesLegacyFormatsAndRecognizesCurrentWriters()
    {
        var service = new GamAssetFileService();
        var v1Path = Path.Combine(testDirectory, "legacy.gam");
        await File.WriteAllTextAsync(
            v1Path,
            "# GAM Collection Export v1\n# Title: Legacy\n# Count: 1\n123\n");
        var v2Path = Path.Combine(testDirectory, "single.gam");
        await File.WriteAllTextAsync(
            v2Path,
            """
            {
              "format": "gam-asset",
              "version": 2,
              "asset": {
                "name": "Previous Single",
                "state": "enabled",
                "membership": { "kind": "fixed", "addonIds": ["456"] }
              }
            }
            """);
        var v3SinglePath = Path.Combine(testDirectory, "current-single.gam");
        await service.WriteAsync(v3SinglePath, CreateDocument("Current Single", "654"));
        var v3BundlePath = Path.Combine(testDirectory, "previous-bundle.gam");
        await File.WriteAllBytesAsync(v3BundlePath, CreateLegacyV3Bundle());
        var v4BundlePath = Path.Combine(testDirectory, "current-bundle.gam");
        await service.WriteBundleAsync(
            v4BundlePath,
            new GamAssetBundleDocument(
                new[]
                {
                    new GamAssetBundleAsset(
                        "asset",
                        "Bundled",
                        GamAssetDocumentState.Disabled,
                        GamAssetDocumentMembership.Fixed(new[] { "789" }))
                },
                Array.Empty<GamAssetBundleGroup>()));

        var v1 = await service.ReadAnyAsync(v1Path);
        var v2 = await service.ReadAnyAsync(v2Path);
        var v3Single = await service.ReadAnyAsync(v3SinglePath);
        var v3Bundle = await service.ReadAnyAsync(v3BundlePath);
        var v4Bundle = await service.ReadAnyAsync(v4BundlePath);

        Assert.Equal(GamAssetFileContentKind.SingleAsset, v1.Kind);
        Assert.Equal(1, v1.SourceFormatVersion);
        Assert.Equal("Legacy", v1.SingleAsset!.Name);
        Assert.Equal(GamAssetFileContentKind.SingleAsset, v2.Kind);
        Assert.Equal(2, v2.SourceFormatVersion);
        Assert.Equal("Previous Single", v2.SingleAsset!.Name);
        Assert.Equal(GamAssetFileContentKind.SingleAsset, v3Single.Kind);
        Assert.Equal(3, v3Single.SourceFormatVersion);
        Assert.Equal("Current Single", v3Single.SingleAsset!.Name);
        Assert.Equal(GamAssetFileContentKind.Bundle, v3Bundle.Kind);
        Assert.Equal(3, v3Bundle.SourceFormatVersion);
        Assert.Equal("Previous Bundled", v3Bundle.Bundle!.Assets[0].Name);
        Assert.Equal(GamAssetFileContentKind.Bundle, v4Bundle.Kind);
        Assert.Equal(4, v4Bundle.SourceFormatVersion);
        Assert.Equal("Bundled", v4Bundle.Bundle!.Assets[0].Name);
    }

    [Fact]
    public async Task WriteBundle_AtomicallyReplacesExistingDocument()
    {
        var path = Path.Combine(testDirectory, "bundle.gam");
        var service = new GamAssetFileService();
        await service.WriteAsync(path, CreateDocument("First", "123"));
        var bundle = new GamAssetBundleDocument(
            new[]
            {
                new GamAssetBundleAsset(
                    "asset",
                    "Replacement",
                    GamAssetDocumentState.Excluded,
                    GamAssetDocumentMembership.Fixed(new[] { "456" }))
            },
            Array.Empty<GamAssetBundleGroup>());

        await service.WriteBundleAsync(path, bundle, overwrite: true);
        var restored = await service.ReadAnyAsync(path);

        Assert.Equal(GamAssetFileContentKind.Bundle, restored.Kind);
        Assert.Equal("Replacement", restored.Bundle!.Assets[0].Name);
        Assert.Single(Directory.EnumerateFiles(testDirectory));
        Assert.Empty(Directory.EnumerateFiles(testDirectory, "*.tmp"));
    }

    [Fact]
    public async Task WriteBundle_InvalidDocument_PreservesExistingFileAndCleansTemp()
    {
        var path = Path.Combine(testDirectory, "bundle.gam");
        var service = new GamAssetFileService();
        await service.WriteAsync(path, CreateDocument("Original", "123"));
        var invalid = new GamAssetBundleDocument(
            Array.Empty<GamAssetBundleAsset>(),
            Array.Empty<GamAssetBundleGroup>());

        await Assert.ThrowsAsync<GamAssetDocumentException>(() =>
            service.WriteBundleAsync(path, invalid, overwrite: true));
        var restored = await service.ReadAsync(path);

        Assert.Equal("Original", restored.Name);
        Assert.Single(Directory.EnumerateFiles(testDirectory));
        Assert.Empty(Directory.EnumerateFiles(testDirectory, "*.tmp"));
    }

    [Fact]
    public async Task Write_AtomicallyReplacesExistingDocument()
    {
        var path = Path.Combine(testDirectory, "asset.gam");
        var service = new GamAssetFileService();
        await service.WriteAsync(path, CreateDocument("First", "123"));

        await service.WriteAsync(path, CreateDocument("Second", "456"), overwrite: true);
        var restored = await service.ReadAsync(path);

        Assert.Equal("Second", restored.Name);
        Assert.Equal(new[] { "456" }, restored.Membership.AddonIds);
        Assert.Single(Directory.EnumerateFiles(testDirectory));
        Assert.Empty(Directory.EnumerateFiles(testDirectory, "*.tmp"));
    }

    [Fact]
    public async Task Write_WithOverwriteDisabled_PreservesExistingDocument()
    {
        var path = Path.Combine(testDirectory, "asset.gam");
        var service = new GamAssetFileService();
        await service.WriteAsync(path, CreateDocument("First", "123"));

        await Assert.ThrowsAsync<IOException>(() =>
            service.WriteAsync(path, CreateDocument("Second", "456"), overwrite: false));
        var restored = await service.ReadAsync(path);

        Assert.Equal("First", restored.Name);
        Assert.Empty(Directory.EnumerateFiles(testDirectory, "*.tmp"));
    }

    [Fact]
    public async Task Write_InvalidDocument_DoesNotLeaveTempOrDestinationFile()
    {
        var path = Path.Combine(testDirectory, "invalid.gam");
        var service = new GamAssetFileService();
        var invalid = CreateDocument("Invalid", "*");

        await Assert.ThrowsAsync<GamAssetDocumentException>(() =>
            service.WriteAsync(path, invalid));

        Assert.False(File.Exists(path));
        Assert.Empty(Directory.EnumerateFiles(testDirectory));
    }

    [Theory]
    [InlineData("asset.json")]
    [InlineData("asset")]
    public async Task ReadAndWrite_RejectNonGamExtensions(string fileName)
    {
        var path = Path.Combine(testDirectory, fileName);
        var service = new GamAssetFileService();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.WriteAsync(path, CreateDocument("Asset", "123")));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReadAsync(path));
    }

    [Fact]
    public async Task Read_V2DoesNotImposeFormerEightMiBDocumentProductCap()
    {
        var path = Path.Combine(testDirectory, "large-v2.gam");
        var json = """
            {
              "format": "gam-asset",
              "version": 2,
              "asset": {
                "name": "Large but valid",
                "state": "enabled",
                "membership": { "kind": "fixed", "addonIds": [] }
              }
            }
            """;
        await using (var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None))
        {
            var prefix = new byte[9 * 1024 * 1024];
            Array.Fill(prefix, (byte)' ');
            await stream.WriteAsync(prefix);
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json));
        }

        var service = new GamAssetFileService();
        var restored = await service.ReadAsync(path);

        Assert.Equal("Large but valid", restored.Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static GamAssetDocument CreateDocument(string name, string addonId)
    {
        return new GamAssetDocument(
            name,
            GamAssetDocumentState.Enabled,
            GamAssetDocumentMembership.Fixed(new[] { addonId }));
    }

    private static byte[] CreateLegacyV3Bundle()
    {
        const string manifest = """
            {
              "format": "gam-asset-bundle",
              "version": 3,
              "assets": [
                {
                  "localId": "asset",
                  "name": "Previous Bundled",
                  "state": "disabled",
                  "membership": { "kind": "fixed", "addonIds": ["789"] }
                }
              ],
              "groups": []
            }
            """;
        var manifestBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes(manifest);
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
            using (var stream = manifestEntry.Open())
            {
                stream.Write(manifestBytes, 0, manifestBytes.Length);
            }

            var checksumEntry = archive.CreateEntry(
                "manifest.sha256",
                CompressionLevel.NoCompression);
            using (var stream = checksumEntry.Open())
            {
                var checksum = Encoding.ASCII.GetBytes(
                    Convert.ToHexString(SHA256.HashData(manifestBytes)));
                stream.Write(checksum, 0, checksum.Length);
            }
        }

        return output.ToArray();
    }
}
