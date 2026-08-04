using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace GmodAddonManager.Core.Tests;

public sealed class GamAssetBundleCodecTests
{
    [Fact]
    public void V4_RoundTripsMemosAndExactMixedNestedTopology()
    {
        var bundle = new GamAssetBundleDocument(
            new[]
            {
                new GamAssetBundleAsset(
                    "asset-root-a",
                    "Root A",
                    GamAssetDocumentState.Enabled,
                    GamAssetDocumentMembership.Fixed(new[] { "300" }),
                    memo: "asset\r\nmemo"),
                Fixed("asset-parent", "Parent Asset", GamAssetDocumentState.Disabled, "100", "200"),
                Smart("asset-child", "Child Asset", GamAssetDocumentState.Excluded),
                Fixed("asset-root-b", "Root B", GamAssetDocumentState.Enabled, "600")
            },
            new[]
            {
                new GamAssetBundleGroup(
                    "group-parent",
                    "Parent Group",
                    GamAssetDocumentState.Excluded,
                    new[]
                    {
                        GamAssetBundleEntryReference.Asset("asset-parent"),
                        GamAssetBundleEntryReference.Group("group-child")
                    },
                    memo: "group\rnotes"),
                new GamAssetBundleGroup(
                    "group-child",
                    "Child Group",
                    GamAssetDocumentState.Disabled,
                    new[] { GamAssetBundleEntryReference.Asset("asset-child") })
            },
            new[]
            {
                GamAssetBundleEntryReference.Asset("asset-root-b"),
                GamAssetBundleEntryReference.Group("group-parent"),
                GamAssetBundleEntryReference.Asset("asset-root-a")
            });

        using var stream = new MemoryStream();
        GamAssetBundleCodec.Serialize(stream, bundle);
        var manifest = ReadManifest(stream.ToArray());
        stream.Position = 0;
        var restored = GamAssetBundleCodec.Deserialize(stream);

        Assert.Equal(4, (int?)manifest["version"]);
        Assert.Equal(4, restored.SourceFormatVersion);
        Assert.Equal(new[] { "asset-root-a", "asset-parent", "asset-child", "asset-root-b" },
            restored.Assets.Select(asset => asset.LocalId));
        Assert.Equal("asset\nmemo", restored.Assets[0].Memo);
        Assert.Equal(GamAssetDocumentMembershipKind.Fixed, restored.Assets[1].Membership.Kind);
        Assert.Equal(new[] { "100", "200" }, restored.Assets[1].Membership.AddonIds);
        Assert.Equal(GamAssetDocumentMembershipKind.Smart, restored.Assets[2].Membership.Kind);
        Assert.Equal(GamAssetDocumentRuleKind.Type, restored.Assets[2].Membership.Rule!.Kind);
        Assert.Equal("Weapon", restored.Assets[2].Membership.Rule!.Value);
        Assert.Equal(new[] { "400", "500" }, restored.Assets[2].Membership.SnapshotAddonIds);
        Assert.Equal(2, restored.Groups.Count);
        Assert.Equal(GamAssetDocumentState.Excluded, restored.Groups[0].DefaultChildState);
        Assert.Equal("group\nnotes", restored.Groups[0].Memo);
        Assert.Equal(
            new[] { "asset:asset-parent", "group:group-child" },
            restored.Groups[0].Children.Select(EntrySignature));
        Assert.Equal(
            new[] { "asset:asset-child" },
            restored.Groups[1].Children.Select(EntrySignature));
        Assert.Equal(
            new[] { "asset:asset-root-b", "group:group-parent", "asset:asset-root-a" },
            restored.RootChildren.Select(EntrySignature));
    }

    [Fact]
    public void AssetAndGroupImages_AreIndependentlyNormalizedAndChecksummed()
    {
        var bundle = new GamAssetBundleDocument(
            new[]
            {
                new GamAssetBundleAsset(
                    "asset-image",
                    "Asset Image",
                    GamAssetDocumentState.Enabled,
                    GamAssetDocumentMembership.Fixed(Array.Empty<string>()),
                    CreatePng(900, 300, SKColors.Blue))
            },
            new[]
            {
                new GamAssetBundleGroup(
                    "group-image",
                    "Group Image",
                    GamAssetDocumentState.Disabled,
                    new[] { "asset-image" },
                    CreatePng(300, 900, SKColors.Orange))
            });

        using var stream = new MemoryStream();
        GamAssetBundleCodec.Serialize(stream, bundle);
        var archiveBytes = stream.ToArray();
        using (var archiveStream = new MemoryStream(archiveBytes, writable: false))
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read))
        {
            Assert.NotNull(archive.GetEntry("images/assets/asset-image.png"));
            Assert.NotNull(archive.GetEntry("images/groups/group-image.png"));
            Assert.NotNull(archive.GetEntry("manifest.sha256"));
        }

        stream.Position = 0;
        var restored = GamAssetBundleCodec.Deserialize(stream);

        AssertNormalizedPng(restored.Assets[0].ImageBytes);
        AssertNormalizedPng(restored.Groups[0].ImageBytes);
    }

    [Fact]
    public void Bundle_DoesNotImposeFormerFiftyThousandAddonProductCap()
    {
        var addonIds = Enumerable.Range(1, 50_001)
            .Select(index => (2_000_000UL + (ulong)index).ToString(
                System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        var bundle = new GamAssetBundleDocument(
            new[]
            {
                new GamAssetBundleAsset(
                    "large-asset",
                    "Large Asset",
                    GamAssetDocumentState.Enabled,
                    GamAssetDocumentMembership.Fixed(addonIds))
            },
            Array.Empty<GamAssetBundleGroup>());

        var validated = GamAssetBundleCodec.ValidateAndNormalize(bundle);
        Assert.Equal(
            addonIds.Length,
            validated.Assets[0].Membership.AddonIds.Count);

        using var stream = new MemoryStream();
        GamAssetBundleCodec.Serialize(stream, bundle);
        stream.Position = 0;
        var restored = GamAssetBundleCodec.Deserialize(stream);

        Assert.Equal(addonIds.Length, restored.Assets[0].Membership.AddonIds.Count);
    }

    [Fact]
    public void Bundle_DoesNotImposeAnArbitraryAssetCountLimit()
    {
        const int count = 50_001;
        var assets = Enumerable.Range(1, count)
            .Select(index => Fixed(
                $"asset-{index}",
                $"Asset {index}",
                GamAssetDocumentState.Enabled))
            .ToArray();

        var validated = GamAssetBundleCodec.ValidateAndNormalize(
            new GamAssetBundleDocument(assets, Array.Empty<GamAssetBundleGroup>()));

        Assert.Equal(count, validated.Assets.Count);
        Assert.Equal(count, validated.RootChildren.Count);
    }

    [Fact]
    public void AssetAndGroupNames_AreUniqueCaseInsensitivelyAcrossKinds()
    {
        var bundle = new GamAssetBundleDocument(
            new[] { Fixed("asset", "Same Name", GamAssetDocumentState.Enabled) },
            new[]
            {
                new GamAssetBundleGroup(
                    "group",
                    "same name",
                    GamAssetDocumentState.Enabled,
                    Array.Empty<string>())
            });

        var error = Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetBundleCodec.Serialize(new MemoryStream(), bundle));

        Assert.Contains("duplicate name", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingDuplicateAndMultiParentChildren_AreRejected()
    {
        var missing = new GamAssetBundleDocument(
            Array.Empty<GamAssetBundleAsset>(),
            new[]
            {
                new GamAssetBundleGroup(
                    "group",
                    "Group",
                    GamAssetDocumentState.Enabled,
                    new[] { "missing" })
            });
        Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetBundleCodec.Serialize(new MemoryStream(), missing));

        var duplicate = new GamAssetBundleDocument(
            new[] { Fixed("asset", "Asset", GamAssetDocumentState.Enabled) },
            new[]
            {
                new GamAssetBundleGroup(
                    "group",
                    "Group",
                    GamAssetDocumentState.Enabled,
                    new[] { "asset", "asset" })
            });
        Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetBundleCodec.Serialize(new MemoryStream(), duplicate));

        var multiParent = new GamAssetBundleDocument(
            new[] { Fixed("asset", "Asset", GamAssetDocumentState.Enabled) },
            new[]
            {
                new GamAssetBundleGroup(
                    "group-a",
                    "Group A",
                    GamAssetDocumentState.Enabled,
                    new[] { "asset" }),
                new GamAssetBundleGroup(
                    "group-b",
                    "Group B",
                    GamAssetDocumentState.Enabled,
                    new[] { "asset" })
            });
        Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetBundleCodec.Serialize(new MemoryStream(), multiParent));
    }

    [Fact]
    public void NestedTopology_RejectsCycleMissingDuplicateMultiParentAndOrphanEntries()
    {
        var cycle = new GamAssetBundleDocument(
            Array.Empty<GamAssetBundleAsset>(),
            new[]
            {
                Group("group-a", "Group A", GamAssetBundleEntryReference.Group("group-b")),
                Group("group-b", "Group B", GamAssetBundleEntryReference.Group("group-a"))
            },
            Array.Empty<GamAssetBundleEntryReference>());
        var cycleError = Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetBundleCodec.ValidateAndNormalize(cycle));
        Assert.Contains("cycle", cycleError.Message, StringComparison.OrdinalIgnoreCase);

        var missing = new GamAssetBundleDocument(
            Array.Empty<GamAssetBundleAsset>(),
            new[]
            {
                Group("group-a", "Group A", GamAssetBundleEntryReference.Group("missing"))
            },
            new[] { GamAssetBundleEntryReference.Group("group-a") });
        var missingError = Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetBundleCodec.ValidateAndNormalize(missing));
        Assert.Contains("missing Group", missingError.Message, StringComparison.OrdinalIgnoreCase);

        var duplicate = new GamAssetBundleDocument(
            Array.Empty<GamAssetBundleAsset>(),
            new[]
            {
                Group(
                    "group-a",
                    "Group A",
                    GamAssetBundleEntryReference.Group("group-b"),
                    GamAssetBundleEntryReference.Group("group-b")),
                Group("group-b", "Group B")
            },
            new[] { GamAssetBundleEntryReference.Group("group-a") });
        var duplicateError = Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetBundleCodec.ValidateAndNormalize(duplicate));
        Assert.Contains("duplicate child", duplicateError.Message, StringComparison.OrdinalIgnoreCase);

        var multiParent = new GamAssetBundleDocument(
            Array.Empty<GamAssetBundleAsset>(),
            new[]
            {
                Group("group-a", "Group A", GamAssetBundleEntryReference.Group("group-c")),
                Group("group-b", "Group B", GamAssetBundleEntryReference.Group("group-c")),
                Group("group-c", "Group C")
            },
            new[]
            {
                GamAssetBundleEntryReference.Group("group-a"),
                GamAssetBundleEntryReference.Group("group-b")
            });
        var multiParentError = Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetBundleCodec.ValidateAndNormalize(multiParent));
        Assert.Contains("more than one parent", multiParentError.Message, StringComparison.OrdinalIgnoreCase);

        var orphan = new GamAssetBundleDocument(
            new[] { Fixed("asset", "Asset", GamAssetDocumentState.Enabled) },
            Array.Empty<GamAssetBundleGroup>(),
            Array.Empty<GamAssetBundleEntryReference>());
        var orphanError = Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetBundleCodec.ValidateAndNormalize(orphan));
        Assert.Contains("missing from", orphanError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NestedTopology_AllowsRootPlusTenNestedLevelsAndRejectsElevenNestedLevels()
    {
        var depthTen = CreateGroupChain(GamAssetBundleCodec.MaximumNestedGroupDepth + 1);
        var validated = GamAssetBundleCodec.ValidateAndNormalize(depthTen);
        Assert.Equal(GamAssetBundleCodec.MaximumNestedGroupDepth + 1, validated.Groups.Count);

        var depthEleven = CreateGroupChain(GamAssetBundleCodec.MaximumNestedGroupDepth + 2);
        var error = Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetBundleCodec.ValidateAndNormalize(depthEleven));

        Assert.Contains("maximum depth", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImportedManifest_DuplicateChildIsRejectedAfterIntegrityVerification()
    {
        var bundle = new GamAssetBundleDocument(
            new[] { Fixed("asset", "Asset", GamAssetDocumentState.Enabled) },
            new[]
            {
                new GamAssetBundleGroup(
                    "group",
                    "Group",
                    GamAssetDocumentState.Disabled,
                    new[] { "asset" })
            });
        using var stream = new MemoryStream();
        GamAssetBundleCodec.Serialize(stream, bundle);
        var malformed = RewriteManifest(stream.ToArray(), manifest =>
            ((JArray)manifest["groups"]![0]!["children"]!).Add(
                new JObject
                {
                    ["kind"] = "asset",
                    ["localId"] = "asset"
                }));

        var error = Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetBundleCodec.Deserialize(new MemoryStream(malformed)));

        Assert.Contains("duplicate child", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImportedManifest_CrossKindDuplicateNameIsRejectedCaseInsensitively()
    {
        var bundle = new GamAssetBundleDocument(
            new[] { Fixed("asset", "Asset Name", GamAssetDocumentState.Enabled) },
            new[]
            {
                new GamAssetBundleGroup(
                    "group",
                    "Group Name",
                    GamAssetDocumentState.Enabled,
                    Array.Empty<string>())
            });
        using var stream = new MemoryStream();
        GamAssetBundleCodec.Serialize(stream, bundle);
        var malformed = RewriteManifest(stream.ToArray(), manifest =>
            manifest["groups"]![0]!["name"] = "asset name");

        var error = Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetBundleCodec.Deserialize(new MemoryStream(malformed)));

        Assert.Contains("duplicate name", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegacyV3Bundle_RemainsReadableWithDerivedFlatRootTopology()
    {
        var restored = GamAssetBundleCodec.Deserialize(
            new MemoryStream(CreateLegacyV3ArchiveBytes()));

        Assert.Equal(3, restored.SourceFormatVersion);
        Assert.Null(restored.Assets[0].Memo);
        Assert.Null(restored.Groups[0].Memo);
        Assert.Equal(
            new[] { "asset:asset-loose", "group:group" },
            restored.RootChildren.Select(EntrySignature));
        Assert.Equal(
            new[] { "asset:asset-grouped" },
            restored.Groups[0].Children.Select(EntrySignature));
    }

    [Fact]
    public void V3RejectsV4Fields_AndV4RejectsLegacyGroupFields()
    {
        var v3WithMemo = RewriteManifest(
            CreateLegacyV3ArchiveBytes(),
            manifest => manifest["assets"]![0]!["memo"] = "not valid in v3");
        var v3Error = Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetBundleCodec.Deserialize(new MemoryStream(v3WithMemo)));
        Assert.Contains("require v4", v3Error.Message, StringComparison.OrdinalIgnoreCase);

        var current = new GamAssetBundleDocument(
            new[] { Fixed("asset", "Asset", GamAssetDocumentState.Enabled) },
            new[] { new GamAssetBundleGroup(
                "group",
                "Group",
                GamAssetDocumentState.Enabled,
                new[] { "asset" }) });
        using var stream = new MemoryStream();
        GamAssetBundleCodec.Serialize(stream, current);
        var v4WithLegacyField = RewriteManifest(
            stream.ToArray(),
            manifest => manifest["groups"]![0]!["childAssetLocalIds"] =
                new JArray("asset"));
        var v4Error = Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetBundleCodec.Deserialize(new MemoryStream(v4WithLegacyField)));
        Assert.Contains("legacy", v4Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V4Memo_MustBeStringAndRespectPortableTextValidation()
    {
        var bytes = CreateArchiveBytes();
        var nonString = RewriteManifest(
            bytes,
            manifest => manifest["assets"]![0]!["memo"] = new JArray("bad"));
        Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetBundleCodec.Deserialize(new MemoryStream(nonString)));

        var tooLong = new GamAssetBundleDocument(
            new[]
            {
                new GamAssetBundleAsset(
                    "asset",
                    "Asset",
                    GamAssetDocumentState.Enabled,
                    GamAssetDocumentMembership.Fixed(Array.Empty<string>()),
                    memo: new string('x', GamAssetDocumentCodec.MaximumMemoLength + 1))
            },
            Array.Empty<GamAssetBundleGroup>());
        Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetBundleCodec.Serialize(new MemoryStream(), tooLong));
    }

    [Fact]
    public void DuplicateArchiveEntry_IsRejected()
    {
        var bytes = CreateArchiveBytes();
        var malformed = RewriteArchive(bytes, archive =>
        {
            var duplicate = archive.CreateEntry("manifest.json");
            using var writer = new StreamWriter(duplicate.Open(), Encoding.UTF8);
            writer.Write("{}");
        });

        var error = Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetBundleCodec.Deserialize(new MemoryStream(malformed)));

        Assert.Contains("duplicate archive entry", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManifestTrailingCommas_AreRejected()
    {
        const string manifest = """
            {
              "format": "gam-asset-bundle",
              "version": 4,
              "assets": [
                {
                  "localId": "asset",
                  "name": "Asset",
                  "state": "enabled",
                  "membership": { "kind": "fixed", "addonIds": ["123"], }
                }
              ],
              "groups": [],
              "rootChildren": [ { "kind": "asset", "localId": "asset" } ]
            }
            """;
        var malformed = CreateArchiveFromManifestBytes(
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(manifest));

        var error = Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetBundleCodec.Deserialize(new MemoryStream(malformed)));

        Assert.Contains("trailing comma", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TraversalArchiveEntry_IsRejectedWithoutExtraction()
    {
        var bytes = CreateArchiveBytes();
        var malformed = RewriteArchive(bytes, archive =>
        {
            var traversal = archive.CreateEntry("../escaped.png");
            using var stream = traversal.Open();
            stream.WriteByte(1);
        });

        var error = Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetBundleCodec.Deserialize(new MemoryStream(malformed)));

        Assert.Contains("unsafe archive entry", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownArchiveEntry_IsRejected()
    {
        var bytes = CreateArchiveBytes();
        var malformed = RewriteArchive(bytes, archive =>
        {
            var unknown = archive.CreateEntry("notes.txt");
            using var stream = unknown.Open();
            stream.WriteByte(1);
        });

        var error = Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetBundleCodec.Deserialize(new MemoryStream(malformed)));

        Assert.Contains("unknown archive entry", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnreferencedOtherwiseValidImageEntry_IsRejected()
    {
        var bytes = CreateArchiveBytes();
        var malformed = RewriteArchive(bytes, archive =>
        {
            var orphan = archive.CreateEntry("images/assets/orphan.png");
            using var stream = orphan.Open();
            var image = CreatePng(16, 16, SKColors.Pink);
            stream.Write(image, 0, image.Length);
        });

        var error = Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetBundleCodec.Deserialize(new MemoryStream(malformed)));

        Assert.Contains("unreferenced image", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingReferencedImageEntry_IsRejected()
    {
        var bundle = new GamAssetBundleDocument(
            new[]
            {
                new GamAssetBundleAsset(
                    "asset-image",
                    "Asset Image",
                    GamAssetDocumentState.Enabled,
                    GamAssetDocumentMembership.Fixed(Array.Empty<string>()),
                    CreatePng(16, 16, SKColors.Purple))
            },
            Array.Empty<GamAssetBundleGroup>());
        using var stream = new MemoryStream();
        GamAssetBundleCodec.Serialize(stream, bundle);
        var malformed = RewriteArchive(
            stream.ToArray(),
            _ => { },
            excludedEntryName: "images/assets/asset-image.png");

        var error = Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetBundleCodec.Deserialize(new MemoryStream(malformed)));

        Assert.Contains("missing image entry", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TruncatedArchive_IsRejected()
    {
        var bytes = CreateArchiveBytes();
        Array.Resize(ref bytes, bytes.Length - 12);

        Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetBundleCodec.Deserialize(new MemoryStream(bytes)));
    }

    [Fact]
    public void ManifestChecksumMismatch_IsRejected()
    {
        var bytes = CreateArchiveBytes();
        var malformed = RewriteArchive(
            bytes,
            archive => ReplaceEntry(archive, "manifest.sha256", Encoding.ASCII.GetBytes(new string('0', 64))),
            excludedEntryName: "manifest.sha256");

        var error = Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetBundleCodec.Deserialize(new MemoryStream(malformed)));

        Assert.Contains("manifest checksum", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImageChecksumMismatch_IsRejected()
    {
        var bundle = new GamAssetBundleDocument(
            new[]
            {
                new GamAssetBundleAsset(
                    "asset-image",
                    "Asset Image",
                    GamAssetDocumentState.Enabled,
                    GamAssetDocumentMembership.Fixed(Array.Empty<string>()),
                    CreatePng(32, 32, SKColors.Green))
            },
            Array.Empty<GamAssetBundleGroup>());
        using var stream = new MemoryStream();
        GamAssetBundleCodec.Serialize(stream, bundle);
        var malformed = RewriteArchive(
            stream.ToArray(),
            archive => ReplaceEntry(
                archive,
                "images/assets/asset-image.png",
                CreatePng(32, 32, SKColors.Red)),
            excludedEntryName: "images/assets/asset-image.png");

        var error = Assert.Throws<GamAssetDocumentException>(() =>
            GamAssetBundleCodec.Deserialize(new MemoryStream(malformed)));

        Assert.Contains("image checksum", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string EntrySignature(GamAssetBundleEntryReference entry)
    {
        return entry.Kind == GamAssetBundleEntryKind.Asset
            ? "asset:" + entry.LocalId
            : "group:" + entry.LocalId;
    }

    private static GamAssetBundleGroup Group(
        string id,
        string name,
        params GamAssetBundleEntryReference[] children)
    {
        return new GamAssetBundleGroup(
            id,
            name,
            GamAssetDocumentState.Enabled,
            children);
    }

    private static GamAssetBundleDocument CreateGroupChain(int groupCount)
    {
        var groups = Enumerable.Range(1, groupCount)
            .Select(index => Group(
                $"group-{index}",
                $"Group {index}",
                index == groupCount
                    ? Array.Empty<GamAssetBundleEntryReference>()
                    : new[] { GamAssetBundleEntryReference.Group($"group-{index + 1}") }))
            .ToArray();
        return new GamAssetBundleDocument(
            Array.Empty<GamAssetBundleAsset>(),
            groups,
            new[] { GamAssetBundleEntryReference.Group("group-1") });
    }

    private static GamAssetBundleAsset Fixed(
        string id,
        string name,
        GamAssetDocumentState state,
        params string[] addonIds)
    {
        return new GamAssetBundleAsset(
            id,
            name,
            state,
            GamAssetDocumentMembership.Fixed(addonIds));
    }

    private static GamAssetBundleAsset Smart(
        string id,
        string name,
        GamAssetDocumentState state)
    {
        return new GamAssetBundleAsset(
            id,
            name,
            state,
            GamAssetDocumentMembership.Smart(
                new GamAssetDocumentRule(GamAssetDocumentRuleKind.Type, "weapon"),
                new[] { "400", "500" }));
    }

    private static byte[] CreateArchiveBytes()
    {
        using var stream = new MemoryStream();
        GamAssetBundleCodec.Serialize(
            stream,
            new GamAssetBundleDocument(
                new[] { Fixed("asset", "Asset", GamAssetDocumentState.Enabled, "123") },
                Array.Empty<GamAssetBundleGroup>()));
        return stream.ToArray();
    }

    private static byte[] CreateLegacyV3ArchiveBytes()
    {
        var manifest = new JObject
        {
            ["format"] = GamAssetBundleCodec.FormatIdentifier,
            ["version"] = 3,
            ["assets"] = new JArray
            {
                new JObject
                {
                    ["localId"] = "asset-grouped",
                    ["name"] = "Grouped",
                    ["state"] = "disabled",
                    ["membership"] = new JObject
                    {
                        ["kind"] = "fixed",
                        ["addonIds"] = new JArray("789")
                    }
                },
                new JObject
                {
                    ["localId"] = "asset-loose",
                    ["name"] = "Loose",
                    ["state"] = "enabled",
                    ["membership"] = new JObject
                    {
                        ["kind"] = "fixed",
                        ["addonIds"] = new JArray("456")
                    }
                }
            },
            ["groups"] = new JArray
            {
                new JObject
                {
                    ["localId"] = "group",
                    ["name"] = "Legacy Group",
                    ["defaultChildState"] = "excluded",
                    ["childAssetLocalIds"] = new JArray("asset-grouped")
                }
            }
        };
        return CreateArchiveFromManifest(manifest);
    }

    private static JObject ReadManifest(byte[] source)
    {
        using var inputStream = new MemoryStream(source, writable: false);
        using var archive = new ZipArchive(inputStream, ZipArchiveMode.Read);
        using var reader = new StreamReader(
            archive.GetEntry("manifest.json")!.Open(),
            Encoding.UTF8);
        return JObject.Parse(reader.ReadToEnd());
    }

    private static byte[] CreateArchiveFromManifest(JObject manifest)
    {
        var manifestBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes(manifest.ToString(Formatting.Indented) + "\n");
        return CreateArchiveFromManifestBytes(manifestBytes);
    }

    private static byte[] CreateArchiveFromManifestBytes(byte[] manifestBytes)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            ReplaceEntry(archive, "manifest.json", manifestBytes);
            ReplaceEntry(
                archive,
                "manifest.sha256",
                Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(manifestBytes))));
        }

        return output.ToArray();
    }

    private static byte[] RewriteArchive(
        byte[] source,
        Action<ZipArchive> mutate,
        string? excludedEntryName = null)
    {
        using var output = new MemoryStream();
        using (var destination = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var inputStream = new MemoryStream(source, writable: false);
            using var input = new ZipArchive(inputStream, ZipArchiveMode.Read);
            foreach (var inputEntry in input.Entries)
            {
                if (string.Equals(inputEntry.FullName, excludedEntryName, StringComparison.Ordinal))
                {
                    continue;
                }

                var outputEntry = destination.CreateEntry(inputEntry.FullName, CompressionLevel.Optimal);
                using var from = inputEntry.Open();
                using var to = outputEntry.Open();
                from.CopyTo(to);
            }

            mutate(destination);
        }

        return output.ToArray();
    }

    private static byte[] RewriteManifest(byte[] source, Action<JObject> mutate)
    {
        var entries = new List<(string Name, byte[] Bytes)>();
        JObject manifest;
        using (var inputStream = new MemoryStream(source, writable: false))
        using (var input = new ZipArchive(inputStream, ZipArchiveMode.Read))
        {
            var manifestEntry = input.GetEntry("manifest.json")!;
            using (var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8))
            {
                manifest = JObject.Parse(reader.ReadToEnd());
            }

            foreach (var entry in input.Entries)
            {
                if (entry.FullName is "manifest.json" or "manifest.sha256")
                {
                    continue;
                }

                using var from = entry.Open();
                using var bytes = new MemoryStream();
                from.CopyTo(bytes);
                entries.Add((entry.FullName, bytes.ToArray()));
            }
        }

        mutate(manifest);
        var manifestBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes(manifest.ToString(Formatting.Indented) + "\n");
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                ReplaceEntry(archive, name, bytes);
            }

            ReplaceEntry(archive, "manifest.json", manifestBytes);
            ReplaceEntry(
                archive,
                "manifest.sha256",
                Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(manifestBytes))));
        }

        return output.ToArray();
    }

    private static void ReplaceEntry(ZipArchive archive, string path, byte[] bytes)
    {
        var replacement = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = replacement.Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    private static byte[] CreatePng(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        Assert.NotNull(data);
        return data.ToArray();
    }

    private static void AssertNormalizedPng(byte[]? bytes)
    {
        Assert.NotNull(bytes);
        Assert.True(bytes!.Length <= GamAssetDocumentImageNormalizer.MaximumOutputBytes);
        using var bitmap = SKBitmap.Decode(bytes);
        Assert.NotNull(bitmap);
        Assert.Equal(GamAssetDocumentImageNormalizer.OutputWidth, bitmap.Width);
        Assert.Equal(GamAssetDocumentImageNormalizer.OutputHeight, bitmap.Height);
    }
}
