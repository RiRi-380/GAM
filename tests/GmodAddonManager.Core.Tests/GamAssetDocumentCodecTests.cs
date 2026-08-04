using System.Text;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace GmodAddonManager.Core.Tests;

public sealed class GamAssetDocumentCodecTests
{
    [Fact]
    public void FixedV3_RoundTripsPortableFieldsOnly()
    {
        var document = new GamAssetDocument(
            "  FPS Set  ",
            GamAssetDocumentState.Excluded,
            GamAssetDocumentMembership.Fixed(new[] { "123", "456" }));

        var bytes = GamAssetDocumentCodec.Serialize(document);
        var json = JObject.Parse(Encoding.UTF8.GetString(bytes));
        var restored = GamAssetDocumentCodec.Deserialize(bytes);

        Assert.Equal("gam-asset", (string?)json["format"]);
        Assert.Equal(3, (int?)json["version"]);
        Assert.Null(json["id"]);
        Assert.Null(json["favorite"]);
        Assert.Null(json["versionHistory"]);
        Assert.Null(json["image"]);
        Assert.Equal("FPS Set", restored.Name);
        Assert.Equal(GamAssetDocumentState.Excluded, restored.State);
        Assert.Equal(GamAssetDocumentMembershipKind.Fixed, restored.Membership.Kind);
        Assert.Equal(new[] { "123", "456" }, restored.Membership.AddonIds);
        Assert.Empty(restored.Membership.SnapshotAddonIds);
        Assert.Null(restored.Membership.Rule);
        Assert.Equal(3, restored.SourceFormatVersion);
    }

    [Fact]
    public void SmartV3_RoundTripsRuleAndInformationalSnapshot()
    {
        var document = new GamAssetDocument(
            "Weapons",
            GamAssetDocumentState.Disabled,
            GamAssetDocumentMembership.Smart(
                new GamAssetDocumentRule(GamAssetDocumentRuleKind.Type, "weapon"),
                new[] { "10", "20" }));

        var bytes = GamAssetDocumentCodec.Serialize(document);
        var restored = GamAssetDocumentCodec.Deserialize(bytes);

        Assert.Equal(GamAssetDocumentMembershipKind.Smart, restored.Membership.Kind);
        Assert.Empty(restored.Membership.AddonIds);
        Assert.NotNull(restored.Membership.Rule);
        Assert.Equal(GamAssetDocumentRuleKind.Type, restored.Membership.Rule!.Kind);
        Assert.Equal("Weapon", restored.Membership.Rule.Value);
        Assert.Equal(new[] { "10", "20" }, restored.Membership.SnapshotAddonIds);
        Assert.Equal(GamAssetDocumentState.Disabled, restored.State);
    }

    [Fact]
    public void V3_RoundTripsCanonicalOptionalMemo_WhileV2RemainsReadable()
    {
        var current = new GamAssetDocument(
            "Notes",
            GamAssetDocumentState.Enabled,
            GamAssetDocumentMembership.Fixed(new[] { "123" }),
            memo: "first\r\nsecond\tline");

        var bytes = GamAssetDocumentCodec.Serialize(current);
        var json = JObject.Parse(Encoding.UTF8.GetString(bytes));
        var restored = GamAssetDocumentCodec.Deserialize(bytes);

        Assert.Equal("first\nsecond\tline", (string?)json["asset"]?["memo"]);
        Assert.Equal("first\nsecond\tline", restored.Memo);
        Assert.Equal(3, restored.SourceFormatVersion);

        const string legacyV2 = """
            {
              "format": "gam-asset",
              "version": 2,
              "asset": {
                "name": "Previous",
                "state": "disabled",
                "membership": { "kind": "fixed", "addonIds": ["456"] }
              }
            }
            """;
        var previous = GamAssetDocumentCodec.Deserialize(Encoding.UTF8.GetBytes(legacyV2));
        Assert.Equal(2, previous.SourceFormatVersion);
        Assert.Null(previous.Memo);
        Assert.Equal(new[] { "456" }, previous.Membership.AddonIds);
    }

    [Fact]
    public void Memo_IsFieldBoundedAndRejectsUnsafeControlCharacters()
    {
        var tooLong = new GamAssetDocument(
            "Memo",
            GamAssetDocumentState.Enabled,
            GamAssetDocumentMembership.Fixed(Array.Empty<string>()),
            memo: new string('x', GamAssetDocumentCodec.MaximumMemoLength + 1));
        var control = new GamAssetDocument(
            "Memo",
            GamAssetDocumentState.Enabled,
            GamAssetDocumentMembership.Fixed(Array.Empty<string>()),
            memo: "unsafe\0memo");

        Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetDocumentCodec.Serialize(tooLong));
        Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetDocumentCodec.Serialize(control));

        const string nonStringMemo = """
            {
              "format": "gam-asset",
              "version": 3,
              "asset": {
                "name": "Memo",
                "memo": ["invalid"],
                "state": "enabled",
                "membership": { "kind": "fixed", "addonIds": [] }
              }
            }
            """;
        Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetDocumentCodec.Deserialize(Encoding.UTF8.GetBytes(nonStringMemo)));
    }

    [Fact]
    public void LegacyV1_ImportsTitleAndIdsAsEnabledFixedAsset()
    {
        const string legacy = """
            # GAM Collection Export v1
            # Title: Friends FPS
            # Description: ignored portable legacy metadata
            # Created: 2025-01-02 03:04:05
            # Count: 3

            123
            456
            123
            """;

        var restored = GamAssetDocumentCodec.Deserialize(Encoding.UTF8.GetBytes(legacy));

        Assert.Equal(1, restored.SourceFormatVersion);
        Assert.Equal("Friends FPS", restored.Name);
        Assert.Equal(GamAssetDocumentState.Enabled, restored.State);
        Assert.Equal(GamAssetDocumentMembershipKind.Fixed, restored.Membership.Kind);
        Assert.Equal(new[] { "123", "456" }, restored.Membership.AddonIds);
        Assert.Null(restored.ImageBytes);
    }

    [Fact]
    public void LegacyV1_WithoutTitle_UsesSafeFallbackName()
    {
        const string legacy = "# GAM Collection Export v1\n# Count: 0\n";

        var restored = GamAssetDocumentCodec.Deserialize(Encoding.UTF8.GetBytes(legacy));

        Assert.Equal("Imported Asset", restored.Name);
        Assert.Empty(restored.Membership.AddonIds);
    }

    [Fact]
    public void LegacyV1_CountMismatch_IsRejected()
    {
        const string legacy = "# GAM Collection Export v1\n# Count: 2\n123\n";

        var error = Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetDocumentCodec.Deserialize(Encoding.UTF8.GetBytes(legacy)));

        Assert.Contains("does not match", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegacyV1_DoesNotImposeFormerFiftyThousandAddonProductCap()
    {
        const int count = 50_001;
        var builder = new StringBuilder("# GAM Collection Export v1\n# Count: 50001\n");
        for (var index = 1; index <= count; index++)
        {
            builder.Append((3_000_000 + index).ToString(
                System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        }

        var restored = GamAssetDocumentCodec.Deserialize(
            Encoding.UTF8.GetBytes(builder.ToString()));

        Assert.Equal(count, restored.Membership.AddonIds.Count);
    }

    [Fact]
    public void FutureVersion_IsRejectedWithoutBestEffortImport()
    {
        const string json = """
            {
              "format": "gam-asset",
              "version": 99,
              "asset": {
                "name": "Future",
                "state": "enabled",
                "membership": { "kind": "fixed", "addonIds": [] }
              }
            }
            """;

        var error = Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetDocumentCodec.Deserialize(Encoding.UTF8.GetBytes(json)));

        Assert.Contains("future version 99", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownField_IsRejected()
    {
        const string json = """
            {
              "format": "gam-asset",
              "version": 2,
              "asset": {
                "name": "Unsafe",
                "state": "enabled",
                "membership": { "kind": "fixed", "addonIds": [] },
                "localPath": "C:\\payload"
              }
            }
            """;

        var error = Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetDocumentCodec.Deserialize(Encoding.UTF8.GetBytes(json)));

        Assert.Contains("unsupported field", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateJsonProperty_IsRejected()
    {
        const string json = """
            {
              "format": "gam-asset",
              "format": "gam-asset",
              "version": 2,
              "asset": {
                "name": "Duplicate",
                "state": "enabled",
                "membership": { "kind": "fixed", "addonIds": [] }
              }
            }
            """;

        Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetDocumentCodec.Deserialize(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void JsonComments_AreRejected()
    {
        const string json = """
            {
              /* comments are not part of the portable JSON format */
              "format": "gam-asset",
              "version": 3,
              "asset": {
                "name": "Commented",
                "state": "disabled",
                "membership": { "kind": "fixed", "addonIds": [] }
              }
            }
            """;

        Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetDocumentCodec.Deserialize(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void JsonTrailingCommas_AreRejected()
    {
        const string json = """
            {
              "format": "gam-asset",
              "version": 3,
              "asset": {
                "name": "Trailing comma",
                "state": "disabled",
                "membership": { "kind": "fixed", "addonIds": [], }
              },
            }
            """;

        Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetDocumentCodec.Deserialize(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("subscribe")]
    [InlineData("local_123")]
    [InlineData("C:\\addon")]
    [InlineData("0")]
    [InlineData("01")]
    [InlineData("18446744073709551616")]
    public void NonWorkshopOrNonCanonicalIds_AreRejected(string addonId)
    {
        var document = new GamAssetDocument(
            "Invalid",
            GamAssetDocumentState.Enabled,
            GamAssetDocumentMembership.Fixed(new[] { addonId }));

        Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetDocumentCodec.Serialize(document));
    }

    [Fact]
    public void DuplicateAddonIds_AreRejectedByCurrentWriter()
    {
        var document = new GamAssetDocument(
            "Duplicate",
            GamAssetDocumentState.Enabled,
            GamAssetDocumentMembership.Fixed(new[] { "123", "123" }));

        Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetDocumentCodec.Serialize(document));
    }

    [Fact]
    public void V3_DoesNotImposeFormerFiftyThousandAddonProductCap()
    {
        var addonIds = Enumerable.Range(1, 50_001)
            .Select(index => (1_000_000UL + (ulong)index).ToString(
                System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        var document = new GamAssetDocument(
            "Large fixed Asset",
            GamAssetDocumentState.Enabled,
            GamAssetDocumentMembership.Fixed(addonIds));

        var restored = GamAssetDocumentCodec.Deserialize(
            GamAssetDocumentCodec.Serialize(document));

        Assert.Equal(addonIds.Length, restored.Membership.AddonIds.Count);
        Assert.Equal(addonIds[0], restored.Membership.AddonIds[0]);
        Assert.Equal(addonIds[^1], restored.Membership.AddonIds[^1]);
    }

    [Fact]
    public void EmbeddedImage_IsNormalizedAndRoundTripsAsBoundedPng()
    {
        var sourceImage = CreatePng(width: 800, height: 400, SKColors.CornflowerBlue);
        var document = new GamAssetDocument(
            "With image",
            GamAssetDocumentState.Enabled,
            GamAssetDocumentMembership.Fixed(new[] { "123" }),
            sourceImage);

        var serialized = GamAssetDocumentCodec.Serialize(document);
        var json = JObject.Parse(Encoding.UTF8.GetString(serialized));
        var restored = GamAssetDocumentCodec.Deserialize(serialized);

        Assert.Equal("image/png", (string?)json["image"]?["mediaType"]);
        Assert.Matches("^[0-9a-f]{64}$", (string?)json["image"]?["sha256"] ?? string.Empty);
        Assert.NotNull(restored.ImageBytes);
        Assert.True(restored.ImageBytes!.Length <= GamAssetDocumentImageNormalizer.MaximumOutputBytes);
        using var bitmap = SKBitmap.Decode(restored.ImageBytes);
        Assert.NotNull(bitmap);
        Assert.Equal(512, bitmap.Width);
        Assert.Equal(512, bitmap.Height);

        json["version"] = 2;
        var previous = GamAssetDocumentCodec.Deserialize(
            Encoding.UTF8.GetBytes(json.ToString()));
        Assert.Equal(2, previous.SourceFormatVersion);
        Assert.NotNull(previous.ImageBytes);
        Assert.Equal(restored.ImageBytes, previous.ImageBytes);
    }

    [Fact]
    public void EmbeddedImage_ChecksumMismatch_IsRejected()
    {
        var document = new GamAssetDocument(
            "With image",
            GamAssetDocumentState.Enabled,
            GamAssetDocumentMembership.Fixed(Array.Empty<string>()),
            CreatePng(32, 32, SKColors.Red));
        var json = JObject.Parse(Encoding.UTF8.GetString(GamAssetDocumentCodec.Serialize(document)));
        json["image"]!["sha256"] = new string('0', 64);

        var error = Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetDocumentCodec.Deserialize(Encoding.UTF8.GetBytes(json.ToString())));

        Assert.Contains("checksum", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmbeddedImage_RejectsNonPngBytesWithPngMediaType()
    {
        var document = new GamAssetDocument(
            "With image",
            GamAssetDocumentState.Enabled,
            GamAssetDocumentMembership.Fixed(Array.Empty<string>()),
            CreatePng(32, 32, SKColors.Red));
        var json = JObject.Parse(Encoding.UTF8.GetString(GamAssetDocumentCodec.Serialize(document)));
        var jpeg = CreateEncodedImage(32, 32, SKColors.Red, SKEncodedImageFormat.Jpeg);
        json["image"]!["data"] = Convert.ToBase64String(jpeg);
        json["image"]!["sha256"] = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(jpeg)).ToLowerInvariant();

        var error = Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetDocumentCodec.Deserialize(Encoding.UTF8.GetBytes(json.ToString())));

        Assert.Contains("PNG", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MalformedEmbeddedImage_IsRejected()
    {
        Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetDocumentImageNormalizer.Normalize(new byte[] { 1, 2, 3, 4 }));
    }

    [Fact]
    public void OversizedImageDimension_IsRejectedBeforePixelDecode()
    {
        var sourceImage = CreatePng(
            GamAssetDocumentImageNormalizer.MaximumDimension + 1,
            1,
            SKColors.Green);

        var error = Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetDocumentImageNormalizer.Normalize(sourceImage));

        Assert.Contains("dimension limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DocumentDefensivelyCopiesImageBytes()
    {
        var source = CreatePng(16, 16, SKColors.Purple);
        var document = new GamAssetDocument(
            "Copy",
            GamAssetDocumentState.Enabled,
            GamAssetDocumentMembership.Fixed(Array.Empty<string>()),
            source);
        source[0] = 0;

        var firstRead = document.ImageBytes!;
        firstRead[0] = 0;
        var secondRead = document.ImageBytes!;

        Assert.NotEqual(0, secondRead[0]);
    }

    private static byte[] CreatePng(int width, int height, SKColor color)
    {
        return CreateEncodedImage(width, height, color, SKEncodedImageFormat.Png);
    }

    private static byte[] CreateEncodedImage(
        int width,
        int height,
        SKColor color,
        SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(color);
        using var data = bitmap.Encode(format, 100);
        Assert.NotNull(data);
        return data.ToArray();
    }
}
