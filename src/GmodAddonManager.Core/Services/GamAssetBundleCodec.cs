using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using GmodAddonManager.Core.Models;
using Newtonsoft.Json;

namespace GmodAddonManager.Core.Services
{
    /// <summary>
    /// Streaming codec for archive-backed .gam bundles. Version 3 is read-only;
    /// writers emit v4 with Memo and an explicit nested mixed-entry topology. The archive is never
    /// extracted to disk. Only the manifest, its checksum, and manifest-referenced
    /// normalized PNG entries are accepted.
    /// </summary>
    public static class GamAssetBundleCodec
    {
        public const string FormatIdentifier = "gam-asset-bundle";
        public const int CurrentFormatVersion = 4;
        public const int LegacyFlatFormatVersion = 3;
        public const int MaximumPortableIdLength = 128;
        public const int MaximumNestedGroupDepth = Configuration.MaximumNestedGroupDepth;
        public const int MaximumManifestBytes = 64 * 1024 * 1024;
        public const int MaximumAssetCount = 100_000;
        public const int MaximumGroupCount = 100_000;
        public const int MaximumTopologyReferenceCount = 1_000_000;
        public const int MaximumMembershipAddonIdCount = 5_000_000;
        public const int MaximumImageCount = 4096;
        public const long MaximumAggregateEncodedImageBytes = 512L * 1024L * 1024L;
        public const long MaximumAggregateNormalizedImageBytes = 512L * 1024L * 1024L;
        public const long MaximumArchiveBytes = 640L * 1024L * 1024L;

        private const string ManifestEntryName = "manifest.json";
        private const string ManifestChecksumEntryName = "manifest.sha256";
        private const string AssetImagePrefix = "images/assets/";
        private const string GroupImagePrefix = "images/groups/";
        private const int MaximumJsonDepth = 24;
        private const int MaximumArchiveEntryNameLength = 512;
        private const int MaximumArchiveEntryCount = MaximumImageCount + 2;
        private const int MaximumCentralDirectoryBytes = 8 * 1024 * 1024;
        private const int EndOfCentralDirectoryMinimumLength = 22;
        private const int MaximumZipCommentLength = ushort.MaxValue;
        private const int BufferSize = 81_920;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

        public static void Serialize(
            Stream destination,
            GamAssetBundleDocument document,
            CancellationToken cancellationToken = default)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            if (!destination.CanWrite)
            {
                throw new ArgumentException("The destination stream is not writable.", nameof(destination));
            }

            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            var validated = ValidateForWrite(document, cancellationToken);
            var assetImages = new Dictionary<string, ImageDescriptor>(StringComparer.Ordinal);
            var groupImages = new Dictionary<string, ImageDescriptor>(StringComparer.Ordinal);
            var imageBudget = new ImageImportBudget();

            using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

            foreach (var asset in validated.Assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceImage = asset.Source.ImageBytes;
                if (sourceImage == null)
                {
                    continue;
                }

                var path = AssetImagePrefix + asset.Source.LocalId + ".png";
                assetImages.Add(
                    asset.Source.LocalId,
                    WriteNormalizedImage(
                        archive,
                        path,
                        sourceImage,
                        imageBudget,
                        cancellationToken));
            }

            foreach (var group in validated.Groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceImage = group.Source.ImageBytes;
                if (sourceImage == null)
                {
                    continue;
                }

                var path = GroupImagePrefix + group.Source.LocalId + ".png";
                groupImages.Add(
                    group.Source.LocalId,
                    WriteNormalizedImage(
                        archive,
                        path,
                        sourceImage,
                        imageBudget,
                        cancellationToken));
            }

            var manifestHash = WriteManifest(
                archive,
                validated,
                assetImages,
                groupImages,
                cancellationToken);
            WriteAsciiEntry(
                archive,
                ManifestChecksumEntryName,
                manifestHash,
                cancellationToken);
        }

        public static GamAssetBundleDocument Deserialize(
            Stream source,
            CancellationToken cancellationToken = default)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (!source.CanRead)
            {
                throw new ArgumentException("The source stream is not readable.", nameof(source));
            }

            try
            {
                ValidateArchiveEnvelope(source, cancellationToken);
                using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
                var entries = IndexAndValidateEntries(archive, cancellationToken);
                if (!entries.TryGetValue(ManifestEntryName, out var manifestEntry) ||
                    !entries.TryGetValue(ManifestChecksumEntryName, out var checksumEntry))
                {
                    throw new GamAssetDocumentException(
                        "The .gam bundle is missing its manifest or manifest checksum.");
                }

                var expectedManifestHash = ReadManifestChecksum(checksumEntry, cancellationToken);
                var parsed = ReadManifest(
                    manifestEntry,
                    expectedManifestHash,
                    cancellationToken);
                ValidateParsedStructure(parsed, cancellationToken);

                var referencedImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var imageBudget = new ImageImportBudget();
                var assets = new List<GamAssetBundleAsset>(parsed.Assets.Count);
                foreach (var asset in parsed.Assets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var image = ReadReferencedImage(
                        entries,
                        asset.Image,
                        AssetImagePrefix + asset.LocalId + ".png",
                        referencedImages,
                        imageBudget,
                        cancellationToken);
                    assets.Add(new GamAssetBundleAsset(
                        asset.LocalId,
                        asset.Name,
                        asset.State,
                        asset.Membership,
                        image,
                        asset.Memo));
                }

                var groups = new List<GamAssetBundleGroup>(parsed.Groups.Count);
                foreach (var group in parsed.Groups)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var image = ReadReferencedImage(
                        entries,
                        group.Image,
                        GroupImagePrefix + group.LocalId + ".png",
                        referencedImages,
                        imageBudget,
                        cancellationToken);
                    groups.Add(new GamAssetBundleGroup(
                        group.LocalId,
                        group.Name,
                        group.DefaultChildState,
                        group.Children,
                        image,
                        group.Memo));
                }

                foreach (var entryName in entries.Keys)
                {
                    if (IsImageEntry(entryName) && !referencedImages.Contains(entryName))
                    {
                        throw new GamAssetDocumentException(
                            $"The .gam bundle contains unreferenced image entry '{entryName}'.");
                    }
                }

                return new GamAssetBundleDocument(
                    assets,
                    groups,
                    parsed.RootChildren,
                    parsed.Version);
            }
            catch (GamAssetDocumentException)
            {
                throw;
            }
            catch (Exception ex) when (
                ex is InvalidDataException ||
                ex is EndOfStreamException ||
                ex is DecoderFallbackException ||
                ex is JsonException ||
                ex is OverflowException ||
                ex is IOException)
            {
                throw new GamAssetDocumentException(
                    "The .gam bundle is incomplete or invalid.",
                    ex);
            }
        }

        /// <summary>
        /// Validates an in-memory bundle at a mutation boundary and returns a
        /// canonical copy without constructing a redundant ZIP archive. Images
        /// are still decoded and normalized independently.
        /// </summary>
        public static GamAssetBundleDocument ValidateAndNormalize(
            GamAssetBundleDocument document,
            CancellationToken cancellationToken = default)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            var validated = ValidateForWrite(document, cancellationToken);
            var assets = new List<GamAssetBundleAsset>(validated.Assets.Count);
            var imageBudget = new ImageImportBudget();
            foreach (var asset in validated.Assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = asset.Source;
                var membership = source.Membership.Kind ==
                    GamAssetDocumentMembershipKind.Fixed
                    ? GamAssetDocumentMembership.Fixed(source.Membership.AddonIds)
                    : GamAssetDocumentMembership.Smart(
                        new GamAssetDocumentRule(
                            source.Membership.Rule!.Kind,
                            asset.CanonicalRuleValue!),
                        source.Membership.SnapshotAddonIds);
                var sourceImage = source.ImageBytes;
                var image = sourceImage == null
                    ? null
                    : GamAssetDocumentImageNormalizer.Normalize(sourceImage);
                if (image != null)
                {
                    imageBudget.AddNormalizedBytes(image.Length);
                }
                assets.Add(new GamAssetBundleAsset(
                    source.LocalId,
                    asset.Name,
                    source.State,
                    membership,
                    image,
                    asset.Memo));
            }

            var groups = new List<GamAssetBundleGroup>(validated.Groups.Count);
            foreach (var group in validated.Groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = group.Source;
                var sourceImage = source.ImageBytes;
                var image = sourceImage == null
                    ? null
                    : GamAssetDocumentImageNormalizer.Normalize(sourceImage);
                if (image != null)
                {
                    imageBudget.AddNormalizedBytes(image.Length);
                }
                groups.Add(new GamAssetBundleGroup(
                    source.LocalId,
                    group.Name,
                    source.DefaultChildState,
                    group.Children,
                    image,
                    group.Memo));
            }

            return new GamAssetBundleDocument(
                assets,
                groups,
                validated.RootChildren,
                CurrentFormatVersion);
        }

        private static ValidatedBundle ValidateForWrite(
            GamAssetBundleDocument document,
            CancellationToken cancellationToken)
        {
            if (document.Assets.Count > MaximumAssetCount)
            {
                throw new GamAssetDocumentException(
                    $"A .gam bundle exceeds the {MaximumAssetCount}-Asset safety limit.");
            }

            if (document.Groups.Count > MaximumGroupCount)
            {
                throw new GamAssetDocumentException(
                    $"A .gam bundle exceeds the {MaximumGroupCount}-Group safety limit.");
            }

            if (document.Assets.Count == 0 && document.Groups.Count == 0)
            {
                throw new GamAssetDocumentException("A .gam bundle cannot be empty.");
            }

            ValidateAggregateResourceCounts(document);

            var localIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var assetsById = new Dictionary<string, GamAssetBundleAsset>(StringComparer.OrdinalIgnoreCase);
            var validatedAssets = new List<ValidatedAsset>(document.Assets.Count);

            foreach (var asset in document.Assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (asset == null)
                {
                    throw new GamAssetDocumentException("A .gam bundle contains a null Asset.");
                }

                var localId = ValidatePortableId(asset.LocalId);
                if (!localIds.Add(localId))
                {
                    throw new GamAssetDocumentException(
                        $"The .gam bundle contains duplicate local ID '{localId}'.");
                }

                var name = NormalizeAndValidateName(asset.Name, "Asset");
                if (!names.Add(name))
                {
                    throw new GamAssetDocumentException(
                        $"The .gam bundle contains duplicate name '{name}'.");
                }

                ValidateState(asset.State, "Asset");
                var canonicalRuleValue = ValidateMembership(
                    asset.Membership,
                    cancellationToken);
                assetsById.Add(localId, asset);
                validatedAssets.Add(new ValidatedAsset(
                    asset,
                    name,
                    canonicalRuleValue,
                    GamAssetDocumentCodec.NormalizeAndValidateMemo(asset.Memo)));
            }

            var groupsById = new Dictionary<string, GamAssetBundleGroup>(StringComparer.OrdinalIgnoreCase);
            var validatedGroups = new List<ValidatedGroup>(document.Groups.Count);
            foreach (var group in document.Groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (group == null)
                {
                    throw new GamAssetDocumentException("A .gam bundle contains a null Group.");
                }

                var localId = ValidatePortableId(group.LocalId);
                if (!localIds.Add(localId))
                {
                    throw new GamAssetDocumentException(
                        $"The .gam bundle contains duplicate local ID '{localId}'.");
                }

                var name = NormalizeAndValidateName(group.Name, "Group");
                if (!names.Add(name))
                {
                    throw new GamAssetDocumentException(
                        $"The .gam bundle contains duplicate name '{name}'.");
                }

                ValidateState(group.DefaultChildState, "Group default child");
                groupsById.Add(localId, group);
                validatedGroups.Add(new ValidatedGroup(
                    group,
                    name,
                    GamAssetDocumentCodec.NormalizeAndValidateMemo(group.Memo)));
            }

            var validatedRoot = ValidateTopology(
                document.RootChildren,
                validatedGroups,
                assetsById,
                groupsById,
                cancellationToken);
            return new ValidatedBundle(validatedAssets, validatedGroups, validatedRoot);
        }

        private static void ValidateArchiveEnvelope(
            Stream source,
            CancellationToken cancellationToken)
        {
            if (!source.CanSeek)
            {
                throw new GamAssetDocumentException(
                    "The .gam bundle stream must be seekable for safe archive validation.");
            }

            var archiveStart = source.Position;
            var archiveLength = source.Length - archiveStart;
            if (archiveLength < EndOfCentralDirectoryMinimumLength)
            {
                throw new GamAssetDocumentException("The .gam bundle ZIP envelope is incomplete.");
            }

            if (archiveLength > MaximumArchiveBytes)
            {
                throw new GamAssetDocumentException(
                    $"The .gam bundle exceeds the {MaximumArchiveBytes}-byte archive safety limit.");
            }

            var tailLength = checked((int)Math.Min(
                archiveLength,
                EndOfCentralDirectoryMinimumLength + MaximumZipCommentLength));
            var tail = new byte[tailLength];
            try
            {
                source.Position = archiveStart + archiveLength - tailLength;
                var offset = 0;
                while (offset < tail.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = source.Read(tail, offset, tail.Length - offset);
                    if (read == 0)
                    {
                        throw new EndOfStreamException(
                            "The .gam bundle ended while reading its ZIP directory.");
                    }

                    offset += read;
                }

                var eocdOffset = FindEndOfCentralDirectory(tail);
                if (eocdOffset < 0)
                {
                    throw new GamAssetDocumentException(
                        "The .gam bundle ZIP directory is missing or has trailing data.");
                }

                var diskNumber = ReadUInt16LittleEndian(tail, eocdOffset + 4);
                var centralDirectoryDisk = ReadUInt16LittleEndian(tail, eocdOffset + 6);
                var entriesOnDisk = ReadUInt16LittleEndian(tail, eocdOffset + 8);
                var totalEntries = ReadUInt16LittleEndian(tail, eocdOffset + 10);
                var centralDirectorySize = ReadUInt32LittleEndian(tail, eocdOffset + 12);
                var centralDirectoryOffset = ReadUInt32LittleEndian(tail, eocdOffset + 16);

                // GAM never emits split or ZIP64 archives. Rejecting those
                // envelopes before ZipArchive constructs its directory table
                // keeps attacker-controlled central-directory memory bounded.
                if (diskNumber != 0 ||
                    centralDirectoryDisk != 0 ||
                    entriesOnDisk != totalEntries ||
                    totalEntries == ushort.MaxValue ||
                    centralDirectorySize == uint.MaxValue ||
                    centralDirectoryOffset == uint.MaxValue)
                {
                    throw new GamAssetDocumentException(
                        "Split and ZIP64 .gam bundles are not supported.");
                }

                if (totalEntries > MaximumArchiveEntryCount)
                {
                    throw new GamAssetDocumentException(
                        $"The .gam bundle exceeds the {MaximumArchiveEntryCount}-entry " +
                        "image/archive safety limit.");
                }

                if (centralDirectorySize > MaximumCentralDirectoryBytes)
                {
                    throw new GamAssetDocumentException(
                        $"The .gam bundle central directory exceeds the " +
                        $"{MaximumCentralDirectoryBytes}-byte safety limit.");
                }

                var eocdAbsoluteOffset = archiveLength - tailLength + eocdOffset;
                if ((ulong)centralDirectoryOffset + centralDirectorySize >
                    (ulong)eocdAbsoluteOffset)
                {
                    throw new GamAssetDocumentException(
                        "The .gam bundle central directory points outside its ZIP envelope.");
                }
            }
            finally
            {
                source.Position = archiveStart;
            }
        }

        private static int FindEndOfCentralDirectory(byte[] tail)
        {
            for (var index = tail.Length - EndOfCentralDirectoryMinimumLength;
                 index >= 0;
                 index--)
            {
                if (tail[index] != 0x50 ||
                    tail[index + 1] != 0x4b ||
                    tail[index + 2] != 0x05 ||
                    tail[index + 3] != 0x06)
                {
                    continue;
                }

                var commentLength = ReadUInt16LittleEndian(tail, index + 20);
                if (index + EndOfCentralDirectoryMinimumLength + commentLength == tail.Length)
                {
                    return index;
                }
            }

            return -1;
        }

        private static ushort ReadUInt16LittleEndian(byte[] bytes, int offset)
        {
            return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
        }

        private static uint ReadUInt32LittleEndian(byte[] bytes, int offset)
        {
            return (uint)(bytes[offset] |
                          (bytes[offset + 1] << 8) |
                          (bytes[offset + 2] << 16) |
                          (bytes[offset + 3] << 24));
        }

        private static void ValidateAggregateResourceCounts(GamAssetBundleDocument document)
        {
            long topologyReferences = document.RootChildren.Count;
            long membershipAddonIds = 0;
            long imageCount = 0;
            long imageBytes = 0;

            if (topologyReferences > MaximumTopologyReferenceCount)
            {
                throw new GamAssetDocumentException(
                    $"A .gam bundle exceeds the {MaximumTopologyReferenceCount}-reference safety limit.");
            }

            foreach (var asset in document.Assets)
            {
                if (asset == null)
                {
                    continue;
                }

                membershipAddonIds += asset.Membership.Kind ==
                    GamAssetDocumentMembershipKind.Fixed
                    ? asset.Membership.AddonIds.Count
                    : asset.Membership.SnapshotAddonIds.Count;
                if (membershipAddonIds > MaximumMembershipAddonIdCount)
                {
                    throw new GamAssetDocumentException(
                        $"A .gam bundle exceeds the {MaximumMembershipAddonIdCount}-addon-ID safety limit.");
                }

                if (asset.ImageBytes != null)
                {
                    imageCount++;
                    imageBytes += asset.ImageBytes.Length;
                    ValidateAggregateImageBudget(imageCount, imageBytes);
                }
            }

            foreach (var group in document.Groups)
            {
                if (group == null)
                {
                    continue;
                }

                topologyReferences += group.Children.Count;
                if (topologyReferences > MaximumTopologyReferenceCount)
                {
                    throw new GamAssetDocumentException(
                        $"A .gam bundle exceeds the {MaximumTopologyReferenceCount}-reference safety limit.");
                }

                if (group.ImageBytes != null)
                {
                    imageCount++;
                    imageBytes += group.ImageBytes.Length;
                    ValidateAggregateImageBudget(imageCount, imageBytes);
                }
            }
        }

        private static void ValidateAggregateImageBudget(long imageCount, long imageBytes)
        {
            if (imageCount > MaximumImageCount)
            {
                throw new GamAssetDocumentException(
                    $"A .gam bundle exceeds the {MaximumImageCount}-image safety limit.");
            }

            if (imageBytes > MaximumAggregateEncodedImageBytes)
            {
                throw new GamAssetDocumentException(
                    $"A .gam bundle exceeds the {MaximumAggregateEncodedImageBytes}-byte aggregate image safety limit.");
            }
        }

        private static IReadOnlyList<GamAssetBundleEntryReference> ValidateTopology(
            IReadOnlyList<GamAssetBundleEntryReference> rootChildren,
            IReadOnlyList<ValidatedGroup> groups,
            IReadOnlyDictionary<string, GamAssetBundleAsset> assetsById,
            IReadOnlyDictionary<string, GamAssetBundleGroup> groupsById,
            CancellationToken cancellationToken)
        {
            var assignedParent = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var groupParents = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            IReadOnlyList<GamAssetBundleEntryReference> ValidateContainer(
                IReadOnlyList<GamAssetBundleEntryReference> entries,
                string owner,
                string? parentGroupId)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var canonical = new List<GamAssetBundleEntryReference>(entries.Count);
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry == null)
                    {
                        throw new GamAssetDocumentException(
                            $"The {owner} topology contains a null child reference.");
                    }
                    if (!Enum.IsDefined(typeof(GamAssetBundleEntryKind), entry.Kind))
                    {
                        throw new GamAssetDocumentException(
                            $"The {owner} topology contains an invalid child kind.");
                    }

                    var requestedId = ValidatePortableId(entry.LocalId);
                    string canonicalId;
                    if (entry.Kind == GamAssetBundleEntryKind.Asset)
                    {
                        if (!assetsById.TryGetValue(requestedId, out var asset))
                        {
                            throw new GamAssetDocumentException(
                                $"The {owner} topology references missing Asset local ID '{requestedId}'.");
                        }
                        canonicalId = asset.LocalId;
                    }
                    else
                    {
                        if (!groupsById.TryGetValue(requestedId, out var group))
                        {
                            throw new GamAssetDocumentException(
                                $"The {owner} topology references missing Group local ID '{requestedId}'.");
                        }
                        canonicalId = group.LocalId;
                    }

                    if (!seen.Add(canonicalId))
                    {
                        throw new GamAssetDocumentException(
                            $"The {owner} topology contains duplicate child '{canonicalId}'.");
                    }
                    if (!assignedParent.TryAdd(canonicalId, parentGroupId))
                    {
                        throw new GamAssetDocumentException(
                            $"Portable entry '{canonicalId}' belongs to more than one parent in the bundle.");
                    }
                    if (entry.Kind == GamAssetBundleEntryKind.Group)
                    {
                        groupParents[canonicalId] = parentGroupId;
                    }

                    canonical.Add(new GamAssetBundleEntryReference(entry.Kind, canonicalId));
                }

                return canonical;
            }

            var canonicalRoot = ValidateContainer(rootChildren, "root", parentGroupId: null);
            foreach (var group in groups)
            {
                group.Children = ValidateContainer(
                    group.Source.Children,
                    $"Group '{group.Name}'",
                    group.Source.LocalId);
            }

            var allIds = assetsById.Keys.Concat(groupsById.Keys).ToList();
            var orphan = allIds.FirstOrDefault(id => !assignedParent.ContainsKey(id));
            if (orphan != null)
            {
                throw new GamAssetDocumentException(
                    $"Portable entry '{orphan}' is missing from the bundle topology.");
            }

            foreach (var groupId in groupsById.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var current = groupId;
                // Configuration semantics count only nesting below a root Group:
                // root = 0, direct child = 1, and so on.
                var depth = 0;
                while (true)
                {
                    if (!path.Add(current))
                    {
                        throw new GamAssetDocumentException(
                            $"The bundle Group topology contains a cycle at '{current}'.");
                    }
                    if (depth > MaximumNestedGroupDepth)
                    {
                        throw new GamAssetDocumentException(
                            $"The bundle Group topology exceeds the maximum depth of {MaximumNestedGroupDepth}.");
                    }
                    if (!groupParents.TryGetValue(current, out var parent) || parent == null)
                    {
                        break;
                    }

                    current = parent;
                    depth++;
                }
            }

            return canonicalRoot;
        }

        private static string? ValidateMembership(
            GamAssetDocumentMembership membership,
            CancellationToken cancellationToken)
        {
            switch (membership.Kind)
            {
                case GamAssetDocumentMembershipKind.Fixed:
                    if (membership.Rule != null || membership.SnapshotAddonIds.Count != 0)
                    {
                        throw new GamAssetDocumentException(
                            "A fixed Asset can contain only fixed addon IDs.");
                    }

                    ValidateAddonIds(membership.AddonIds, "addonIds", cancellationToken);
                    return null;

                case GamAssetDocumentMembershipKind.Smart:
                    if (membership.Rule == null || membership.AddonIds.Count != 0)
                    {
                        throw new GamAssetDocumentException(
                            "A Smart Asset must contain exactly one rule.");
                    }

                    if (!Enum.IsDefined(typeof(GamAssetDocumentRuleKind), membership.Rule.Kind))
                    {
                        throw new GamAssetDocumentException("The Smart Asset rule kind is invalid.");
                    }

                    ValidateAddonIds(
                        membership.SnapshotAddonIds,
                        "snapshotAddonIds",
                        cancellationToken);
                    return GamAssetDocumentCodec.CanonicalizeRuleValue(
                        membership.Rule.Kind,
                        membership.Rule.Value);

                default:
                    throw new GamAssetDocumentException("The Asset membership kind is invalid.");
            }
        }

        private static void ValidateAddonIds(
            IReadOnlyList<string> addonIds,
            string fieldName,
            CancellationToken cancellationToken)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var addonId in addonIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var validated = ValidateWorkshopId(addonId);
                if (!seen.Add(validated))
                {
                    throw new GamAssetDocumentException(
                        $"The {fieldName} entry contains duplicate addon ID '{validated}'.");
                }
            }
        }

        private static ImageDescriptor WriteNormalizedImage(
            ZipArchive archive,
            string path,
            byte[] sourceImage,
            ImageImportBudget imageBudget,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = GamAssetDocumentImageNormalizer.Normalize(sourceImage);
            imageBudget.AddNormalizedBytes(normalized.Length);
            var hash = ComputeSha256(normalized);
            var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
            using (var stream = entry.Open())
            {
                WriteBytes(stream, normalized, cancellationToken);
            }

            return new ImageDescriptor(path, hash);
        }

        private static string WriteManifest(
            ZipArchive archive,
            ValidatedBundle document,
            IReadOnlyDictionary<string, ImageDescriptor> assetImages,
            IReadOnlyDictionary<string, ImageDescriptor> groupImages,
            CancellationToken cancellationToken)
        {
            var entry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
            using var sha256 = SHA256.Create();
            using (var entryStream = entry.Open())
            using (var boundedStream = new BoundedWriteStream(
                entryStream,
                MaximumManifestBytes,
                ManifestEntryName,
                cancellationToken))
            using (var hashingStream = new CryptoStream(boundedStream, sha256, CryptoStreamMode.Write))
            {
                using (var textWriter = new StreamWriter(
                    hashingStream,
                    StrictUtf8,
                    BufferSize,
                    leaveOpen: true))
                using (var writer = new JsonTextWriter(textWriter)
                {
                    Formatting = Formatting.Indented,
                    Culture = CultureInfo.InvariantCulture,
                    CloseOutput = false
                })
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("format");
                    writer.WriteValue(FormatIdentifier);
                    writer.WritePropertyName("version");
                    writer.WriteValue(CurrentFormatVersion);

                    writer.WritePropertyName("assets");
                    writer.WriteStartArray();
                    foreach (var asset in document.Assets)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        writer.WriteStartObject();
                        writer.WritePropertyName("localId");
                        writer.WriteValue(asset.Source.LocalId);
                        writer.WritePropertyName("name");
                        writer.WriteValue(asset.Name);
                        if (asset.Memo != null)
                        {
                            writer.WritePropertyName("memo");
                            writer.WriteValue(asset.Memo);
                        }
                        writer.WritePropertyName("state");
                        writer.WriteValue(StateToWireValue(asset.Source.State));
                        writer.WritePropertyName("membership");
                        WriteMembership(writer, asset, cancellationToken);
                        if (assetImages.TryGetValue(asset.Source.LocalId, out var image))
                        {
                            writer.WritePropertyName("image");
                            WriteImageDescriptor(writer, image);
                        }

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WritePropertyName("groups");
                    writer.WriteStartArray();
                    foreach (var group in document.Groups)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        writer.WriteStartObject();
                        writer.WritePropertyName("localId");
                        writer.WriteValue(group.Source.LocalId);
                        writer.WritePropertyName("name");
                        writer.WriteValue(group.Name);
                        if (group.Memo != null)
                        {
                            writer.WritePropertyName("memo");
                            writer.WriteValue(group.Memo);
                        }
                        writer.WritePropertyName("defaultChildState");
                        writer.WriteValue(StateToWireValue(group.Source.DefaultChildState));
                        writer.WritePropertyName("children");
                        WriteEntryReferences(writer, group.Children, cancellationToken);
                        if (groupImages.TryGetValue(group.Source.LocalId, out var image))
                        {
                            writer.WritePropertyName("image");
                            WriteImageDescriptor(writer, image);
                        }

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WritePropertyName("rootChildren");
                    WriteEntryReferences(writer, document.RootChildren, cancellationToken);
                    writer.WriteEndObject();
                    writer.WriteWhitespace("\n");
                    writer.Flush();
                    textWriter.Flush();
                }

                hashingStream.FlushFinalBlock();
            }

            return ToHex(sha256.Hash ?? throw new GamAssetDocumentException(
                "The .gam manifest checksum could not be calculated."));
        }

        private static void WriteEntryReferences(
            JsonTextWriter writer,
            IEnumerable<GamAssetBundleEntryReference> entries,
            CancellationToken cancellationToken)
        {
            writer.WriteStartArray();
            foreach (var child in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteStartObject();
                writer.WritePropertyName("kind");
                writer.WriteValue(child.Kind == GamAssetBundleEntryKind.Asset
                    ? "asset"
                    : "group");
                writer.WritePropertyName("localId");
                writer.WriteValue(child.LocalId);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        private static void WriteMembership(
            JsonTextWriter writer,
            ValidatedAsset asset,
            CancellationToken cancellationToken)
        {
            var membership = asset.Source.Membership;
            writer.WriteStartObject();
            writer.WritePropertyName("kind");
            writer.WriteValue(membership.Kind == GamAssetDocumentMembershipKind.Fixed
                ? "fixed"
                : "smart");
            if (membership.Kind == GamAssetDocumentMembershipKind.Fixed)
            {
                writer.WritePropertyName("addonIds");
                writer.WriteStartArray();
                foreach (var addonId in membership.AddonIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.WriteValue(addonId);
                }

                writer.WriteEndArray();
            }
            else
            {
                writer.WritePropertyName("rule");
                writer.WriteStartObject();
                writer.WritePropertyName("kind");
                writer.WriteValue(membership.Rule!.Kind == GamAssetDocumentRuleKind.Type
                    ? "type"
                    : "tag");
                writer.WritePropertyName("value");
                writer.WriteValue(asset.CanonicalRuleValue);
                writer.WriteEndObject();
                writer.WritePropertyName("snapshotAddonIds");
                writer.WriteStartArray();
                foreach (var addonId in membership.SnapshotAddonIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.WriteValue(addonId);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        private static void WriteImageDescriptor(
            JsonTextWriter writer,
            ImageDescriptor image)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("path");
            writer.WriteValue(image.Path);
            writer.WritePropertyName("mediaType");
            writer.WriteValue("image/png");
            writer.WritePropertyName("sha256");
            writer.WriteValue(image.Sha256);
            writer.WriteEndObject();
        }

        private static void WriteAsciiEntry(
            ZipArchive archive,
            string name,
            string text,
            CancellationToken cancellationToken)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            using var stream = entry.Open();
            WriteBytes(stream, bytes, cancellationToken);
        }

        private static void WriteBytes(
            Stream stream,
            byte[] bytes,
            CancellationToken cancellationToken)
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(BufferSize, bytes.Length - offset);
                stream.Write(bytes, offset, count);
                offset += count;
            }
        }

        private static Dictionary<string, ZipArchiveEntry> IndexAndValidateEntries(
            ZipArchive archive,
            CancellationToken cancellationToken)
        {
            if (archive.Entries.Count > MaximumImageCount + 2)
            {
                throw new GamAssetDocumentException(
                    $"The .gam bundle exceeds the {MaximumImageCount}-image archive safety limit.");
            }

            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            var imageCount = 0;
            long aggregateImageBytes = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = entry.FullName;
                if (!IsSafeArchiveEntryName(name))
                {
                    throw new GamAssetDocumentException(
                        $"The .gam bundle contains unsafe archive entry '{name}'.");
                }

                if (!IsKnownArchiveEntryName(name))
                {
                    throw new GamAssetDocumentException(
                        $"The .gam bundle contains unknown archive entry '{name}'.");
                }

                if (!entries.TryAdd(name, entry))
                {
                    throw new GamAssetDocumentException(
                        $"The .gam bundle contains duplicate archive entry '{name}'.");
                }

                if (IsImageEntry(name))
                {
                    imageCount++;
                    if (entry.Length < 0 ||
                        entry.Length > GamAssetDocumentImageNormalizer.MaximumInputBytes)
                    {
                        throw new GamAssetDocumentException(
                            $"Archive entry '{name}' exceeds its " +
                            $"{GamAssetDocumentImageNormalizer.MaximumInputBytes}-byte limit.");
                    }

                    if (aggregateImageBytes > MaximumAggregateEncodedImageBytes - entry.Length)
                    {
                        throw new GamAssetDocumentException(
                            $"The .gam bundle exceeds the {MaximumAggregateEncodedImageBytes}-byte " +
                            "aggregate image safety limit.");
                    }

                    aggregateImageBytes += entry.Length;
                    ValidateAggregateImageBudget(imageCount, aggregateImageBytes);
                }
            }

            return entries;
        }

        private static bool IsSafeArchiveEntryName(string name)
        {
            if (string.IsNullOrEmpty(name) ||
                name.Length > MaximumArchiveEntryNameLength ||
                name[0] == '/' ||
                name.IndexOf('\\') >= 0 ||
                name.IndexOf(':') >= 0 ||
                name.EndsWith("/", StringComparison.Ordinal))
            {
                return false;
            }

            var segments = name.Split('/');
            return segments.All(segment =>
                segment.Length > 0 &&
                !string.Equals(segment, ".", StringComparison.Ordinal) &&
                !string.Equals(segment, "..", StringComparison.Ordinal));
        }

        private static bool IsKnownArchiveEntryName(string name)
        {
            if (string.Equals(name, ManifestEntryName, StringComparison.Ordinal) ||
                string.Equals(name, ManifestChecksumEntryName, StringComparison.Ordinal))
            {
                return true;
            }

            return TryGetImageLocalId(name, AssetImagePrefix, out _) ||
                TryGetImageLocalId(name, GroupImagePrefix, out _);
        }

        private static bool IsImageEntry(string name)
        {
            return name.StartsWith(AssetImagePrefix, StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(GroupImagePrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetImageLocalId(
            string path,
            string prefix,
            out string localId)
        {
            localId = string.Empty;
            if (!path.StartsWith(prefix, StringComparison.Ordinal) ||
                !path.EndsWith(".png", StringComparison.Ordinal))
            {
                return false;
            }

            var candidate = path.Substring(prefix.Length, path.Length - prefix.Length - 4);
            try
            {
                localId = ValidatePortableId(candidate);
                return true;
            }
            catch (GamAssetDocumentException)
            {
                return false;
            }
        }

        private static string ReadManifestChecksum(
            ZipArchiveEntry entry,
            CancellationToken cancellationToken)
        {
            if (entry.Length != 64)
            {
                throw new GamAssetDocumentException(
                    "The .gam bundle manifest checksum entry has an invalid length.");
            }

            var bytes = ReadBoundedEntry(entry, 64, cancellationToken);
            string value;
            try
            {
                value = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException ex)
            {
                throw new GamAssetDocumentException(
                    "The .gam bundle manifest checksum is not valid UTF-8.",
                    ex);
            }

            ValidateSha256(value, "manifest");
            return value;
        }

        private static ParsedManifest ReadManifest(
            ZipArchiveEntry entry,
            string expectedHash,
            CancellationToken cancellationToken)
        {
            if (entry.Length < 0 || entry.Length > MaximumManifestBytes)
            {
                throw new GamAssetDocumentException(
                    $"The .gam bundle manifest exceeds the {MaximumManifestBytes}-byte safety limit.");
            }

            using var sha256 = SHA256.Create();
            ParsedManifest parsed;
            using (var entryStream = entry.Open())
            using (var boundedStream = new BoundedReadStream(
                entryStream,
                MaximumManifestBytes,
                entry.FullName,
                cancellationToken))
            using (var hashingStream = new CryptoStream(boundedStream, sha256, CryptoStreamMode.Read))
            using (var textReader = new StreamReader(
                hashingStream,
                StrictUtf8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: BufferSize,
                leaveOpen: true))
            using (var strictReader = new GamAssetStrictJsonTextReader(
                textReader,
                cancellationToken))
            using (var reader = new JsonTextReader(strictReader)
            {
                DateParseHandling = DateParseHandling.None,
                FloatParseHandling = FloatParseHandling.Decimal,
                MaxDepth = MaximumJsonDepth,
                CloseInput = false
            })
            {
                parsed = ParseManifest(reader, cancellationToken);
                if (ReadToken(reader, cancellationToken))
                {
                    throw new GamAssetDocumentException(
                        "The .gam bundle manifest contains trailing JSON content.");
                }
            }

            var actualHash = ToHex(sha256.Hash ?? throw new GamAssetDocumentException(
                "The .gam bundle manifest checksum could not be calculated."));
            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new GamAssetDocumentException(
                    "The .gam bundle manifest checksum does not match.");
            }

            return parsed;
        }

        private static ParsedManifest ParseManifest(
            JsonTextReader reader,
            CancellationToken cancellationToken)
        {
            ReadRequiredToken(reader, cancellationToken, "manifest object");
            RequireToken(reader, JsonToken.StartObject, "manifest object");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? format = null;
            int? version = null;
            List<ParsedAsset>? assets = null;
            List<ParsedGroup>? groups = null;
            List<GamAssetBundleEntryReference>? rootChildren = null;
            var rootChildrenSeen = false;

            while (true)
            {
                ReadRequiredToken(reader, cancellationToken, "manifest object");
                if (reader.TokenType == JsonToken.EndObject)
                {
                    break;
                }

                RequireToken(reader, JsonToken.PropertyName, "manifest property");
                var propertyName = (string?)reader.Value ?? string.Empty;
                if (!seen.Add(propertyName))
                {
                    throw new GamAssetDocumentException(
                        $"The .gam bundle manifest contains duplicate field '{propertyName}'.");
                }

                ReadRequiredToken(reader, cancellationToken, propertyName);
                switch (propertyName)
                {
                    case "format":
                        format = ReadString(reader, "format");
                        break;
                    case "version":
                        version = ReadInt32(reader, "version");
                        break;
                    case "assets":
                        assets = ParseAssets(reader, cancellationToken);
                        break;
                    case "groups":
                        groups = ParseGroups(reader, cancellationToken);
                        break;
                    case "rootChildren":
                        rootChildren = ParseEntryReferences(
                            reader,
                            "rootChildren",
                            cancellationToken);
                        rootChildrenSeen = true;
                        break;
                    default:
                        throw new GamAssetDocumentException(
                            $"The .gam bundle manifest contains unsupported field '{propertyName}'.");
                }
            }

            if (format == null || version == null || assets == null || groups == null)
            {
                throw new GamAssetDocumentException(
                    "The .gam bundle manifest is missing a required field.");
            }

            if (!string.Equals(format, FormatIdentifier, StringComparison.Ordinal))
            {
                throw new GamAssetDocumentException(
                    "The .gam bundle manifest identifier is invalid.");
            }

            if (version.Value > CurrentFormatVersion)
            {
                throw new GamAssetDocumentException(
                    $"This .gam bundle uses unsupported future version {version.Value}.");
            }

            if (version.Value != LegacyFlatFormatVersion &&
                version.Value != CurrentFormatVersion)
            {
                throw new GamAssetDocumentException(
                    $"Archive-backed .gam version {version.Value} is not supported.");
            }

            if (version.Value == LegacyFlatFormatVersion)
            {
                if (rootChildrenSeen ||
                    assets.Any(asset => asset.MemoSeen) ||
                    groups.Any(group => group.MemoSeen || group.ChildrenSeen) ||
                    groups.Any(group => !group.LegacyChildIdsSeen))
                {
                    throw new GamAssetDocumentException(
                        "The .gam bundle manifest contains fields that require v4.");
                }

                foreach (var group in groups)
                {
                    group.Children = group.LegacyChildAssetLocalIds!
                        .Select(GamAssetBundleEntryReference.Asset)
                        .ToList();
                }
                var assignedAssets = new HashSet<string>(
                    groups.SelectMany(group => group.LegacyChildAssetLocalIds!),
                    StringComparer.OrdinalIgnoreCase);
                rootChildren = assets
                    .Where(asset => !assignedAssets.Contains(asset.LocalId))
                    .Select(asset => GamAssetBundleEntryReference.Asset(asset.LocalId))
                    .Concat(groups.Select(group =>
                        GamAssetBundleEntryReference.Group(group.LocalId)))
                    .ToList();
            }
            else
            {
                if (!rootChildrenSeen ||
                    groups.Any(group => !group.ChildrenSeen || group.LegacyChildIdsSeen))
                {
                    throw new GamAssetDocumentException(
                        "The .gam v4 manifest is missing its topology or uses legacy Group fields.");
                }
            }

            ValidateParsedResourceCounts(
                assets,
                groups,
                rootChildren ?? new List<GamAssetBundleEntryReference>());

            return new ParsedManifest(
                version.Value,
                assets,
                groups,
                rootChildren ?? new List<GamAssetBundleEntryReference>());
        }

        private static List<ParsedAsset> ParseAssets(
            JsonTextReader reader,
            CancellationToken cancellationToken)
        {
            RequireToken(reader, JsonToken.StartArray, "assets array");
            var assets = new List<ParsedAsset>();
            while (true)
            {
                ReadRequiredToken(reader, cancellationToken, "assets array");
                if (reader.TokenType == JsonToken.EndArray)
                {
                    return assets;
                }

                if (assets.Count >= MaximumAssetCount)
                {
                    throw new GamAssetDocumentException(
                        $"The .gam bundle exceeds the {MaximumAssetCount}-Asset safety limit.");
                }

                assets.Add(ParseAsset(reader, cancellationToken));
            }
        }

        private static ParsedAsset ParseAsset(
            JsonTextReader reader,
            CancellationToken cancellationToken)
        {
            RequireToken(reader, JsonToken.StartObject, "Asset object");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? localId = null;
            string? name = null;
            GamAssetDocumentState? state = null;
            GamAssetDocumentMembership? membership = null;
            string? memo = null;
            var memoSeen = false;
            ImageDescriptor? image = null;
            var imageSeen = false;

            while (true)
            {
                ReadRequiredToken(reader, cancellationToken, "Asset object");
                if (reader.TokenType == JsonToken.EndObject)
                {
                    break;
                }

                var property = ReadUniqueProperty(reader, seen, "Asset");
                ReadRequiredToken(reader, cancellationToken, property);
                switch (property)
                {
                    case "localId":
                        localId = ValidatePortableId(ReadString(reader, property));
                        break;
                    case "name":
                        name = NormalizeAndValidateName(ReadString(reader, property), "Asset");
                        break;
                    case "memo":
                        memo = GamAssetDocumentCodec.NormalizeAndValidateMemo(
                            ReadString(reader, property));
                        memoSeen = true;
                        break;
                    case "state":
                        state = ParseState(ReadString(reader, property), "Asset");
                        break;
                    case "membership":
                        membership = ParseMembership(reader, cancellationToken);
                        break;
                    case "image":
                        image = ParseImageDescriptor(reader, cancellationToken);
                        imageSeen = true;
                        break;
                    default:
                        throw UnsupportedField("Asset", property);
                }
            }

            if (localId == null || name == null || state == null || membership == null)
            {
                throw new GamAssetDocumentException("A .gam bundle Asset is missing a required field.");
            }

            return new ParsedAsset(
                localId,
                name,
                state.Value,
                membership,
                memo,
                memoSeen,
                imageSeen ? image : null);
        }

        private static List<ParsedGroup> ParseGroups(
            JsonTextReader reader,
            CancellationToken cancellationToken)
        {
            RequireToken(reader, JsonToken.StartArray, "groups array");
            var groups = new List<ParsedGroup>();
            while (true)
            {
                ReadRequiredToken(reader, cancellationToken, "groups array");
                if (reader.TokenType == JsonToken.EndArray)
                {
                    return groups;
                }

                if (groups.Count >= MaximumGroupCount)
                {
                    throw new GamAssetDocumentException(
                        $"The .gam bundle exceeds the {MaximumGroupCount}-Group safety limit.");
                }

                groups.Add(ParseGroup(reader, cancellationToken));
            }
        }

        private static ParsedGroup ParseGroup(
            JsonTextReader reader,
            CancellationToken cancellationToken)
        {
            RequireToken(reader, JsonToken.StartObject, "Group object");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? localId = null;
            string? name = null;
            GamAssetDocumentState? defaultState = null;
            string? memo = null;
            var memoSeen = false;
            List<string>? legacyChildIds = null;
            var legacyChildIdsSeen = false;
            List<GamAssetBundleEntryReference>? children = null;
            var childrenSeen = false;
            ImageDescriptor? image = null;
            var imageSeen = false;

            while (true)
            {
                ReadRequiredToken(reader, cancellationToken, "Group object");
                if (reader.TokenType == JsonToken.EndObject)
                {
                    break;
                }

                var property = ReadUniqueProperty(reader, seen, "Group");
                ReadRequiredToken(reader, cancellationToken, property);
                switch (property)
                {
                    case "localId":
                        localId = ValidatePortableId(ReadString(reader, property));
                        break;
                    case "name":
                        name = NormalizeAndValidateName(ReadString(reader, property), "Group");
                        break;
                    case "memo":
                        memo = GamAssetDocumentCodec.NormalizeAndValidateMemo(
                            ReadString(reader, property));
                        memoSeen = true;
                        break;
                    case "defaultChildState":
                        defaultState = ParseState(ReadString(reader, property), "Group default child");
                        break;
                    case "childAssetLocalIds":
                        legacyChildIds = ParsePortableIdArray(reader, property, cancellationToken);
                        legacyChildIdsSeen = true;
                        break;
                    case "children":
                        children = ParseEntryReferences(reader, property, cancellationToken);
                        childrenSeen = true;
                        break;
                    case "image":
                        image = ParseImageDescriptor(reader, cancellationToken);
                        imageSeen = true;
                        break;
                    default:
                        throw UnsupportedField("Group", property);
                }
            }

            if (localId == null || name == null || defaultState == null)
            {
                throw new GamAssetDocumentException("A .gam Group is missing a required field.");
            }

            return new ParsedGroup(
                localId,
                name,
                defaultState.Value,
                memo,
                memoSeen,
                legacyChildIds,
                legacyChildIdsSeen,
                children,
                childrenSeen,
                imageSeen ? image : null);
        }

        private static List<GamAssetBundleEntryReference> ParseEntryReferences(
            JsonTextReader reader,
            string owner,
            CancellationToken cancellationToken)
        {
            RequireToken(reader, JsonToken.StartArray, owner + " array");
            var entries = new List<GamAssetBundleEntryReference>();
            while (true)
            {
                ReadRequiredToken(reader, cancellationToken, owner + " array");
                if (reader.TokenType == JsonToken.EndArray)
                {
                    return entries;
                }

                if (entries.Count >= MaximumTopologyReferenceCount)
                {
                    throw new GamAssetDocumentException(
                        $"The .gam bundle exceeds the {MaximumTopologyReferenceCount}-reference safety limit.");
                }

                RequireToken(reader, JsonToken.StartObject, owner + " child object");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                string? kind = null;
                string? localId = null;
                while (true)
                {
                    ReadRequiredToken(reader, cancellationToken, owner + " child object");
                    if (reader.TokenType == JsonToken.EndObject)
                    {
                        break;
                    }

                    var property = ReadUniqueProperty(reader, seen, owner + " child");
                    ReadRequiredToken(reader, cancellationToken, property);
                    switch (property)
                    {
                        case "kind":
                            kind = ReadString(reader, property);
                            break;
                        case "localId":
                            localId = ValidatePortableId(ReadString(reader, property));
                            break;
                        default:
                            throw UnsupportedField(owner + " child", property);
                    }
                }

                if (kind == null || localId == null)
                {
                    throw new GamAssetDocumentException(
                        $"A .gam {owner} child is missing a required field.");
                }
                var entryKind = kind switch
                {
                    "asset" => GamAssetBundleEntryKind.Asset,
                    "group" => GamAssetBundleEntryKind.Group,
                    _ => throw new GamAssetDocumentException(
                        $"The .gam {owner} child kind is invalid.")
                };
                entries.Add(new GamAssetBundleEntryReference(entryKind, localId));
            }
        }

        private static GamAssetDocumentMembership ParseMembership(
            JsonTextReader reader,
            CancellationToken cancellationToken)
        {
            RequireToken(reader, JsonToken.StartObject, "membership object");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? kind = null;
            List<string>? addonIds = null;
            GamAssetDocumentRule? rule = null;
            List<string>? snapshotIds = null;

            while (true)
            {
                ReadRequiredToken(reader, cancellationToken, "membership object");
                if (reader.TokenType == JsonToken.EndObject)
                {
                    break;
                }

                var property = ReadUniqueProperty(reader, seen, "membership");
                ReadRequiredToken(reader, cancellationToken, property);
                switch (property)
                {
                    case "kind":
                        kind = ReadString(reader, property);
                        break;
                    case "addonIds":
                        addonIds = ParseWorkshopIdArray(reader, property, cancellationToken);
                        break;
                    case "rule":
                        rule = ParseRule(reader, cancellationToken);
                        break;
                    case "snapshotAddonIds":
                        snapshotIds = ParseWorkshopIdArray(reader, property, cancellationToken);
                        break;
                    default:
                        throw UnsupportedField("membership", property);
                }
            }

            if (string.Equals(kind, "fixed", StringComparison.Ordinal))
            {
                if (addonIds == null || rule != null || snapshotIds != null)
                {
                    throw new GamAssetDocumentException(
                        "A fixed .gam bundle Asset has invalid membership fields.");
                }

                return GamAssetDocumentMembership.Fixed(addonIds);
            }

            if (string.Equals(kind, "smart", StringComparison.Ordinal))
            {
                if (addonIds != null || rule == null || snapshotIds == null)
                {
                    throw new GamAssetDocumentException(
                        "A Smart .gam bundle Asset has invalid membership fields.");
                }

                return GamAssetDocumentMembership.Smart(rule, snapshotIds);
            }

            throw new GamAssetDocumentException("The .gam bundle membership kind is invalid.");
        }

        private static GamAssetDocumentRule ParseRule(
            JsonTextReader reader,
            CancellationToken cancellationToken)
        {
            RequireToken(reader, JsonToken.StartObject, "rule object");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? kind = null;
            string? value = null;
            while (true)
            {
                ReadRequiredToken(reader, cancellationToken, "rule object");
                if (reader.TokenType == JsonToken.EndObject)
                {
                    break;
                }

                var property = ReadUniqueProperty(reader, seen, "rule");
                ReadRequiredToken(reader, cancellationToken, property);
                switch (property)
                {
                    case "kind":
                        kind = ReadString(reader, property);
                        break;
                    case "value":
                        value = ReadString(reader, property);
                        break;
                    default:
                        throw UnsupportedField("rule", property);
                }
            }

            if (kind == null || value == null)
            {
                throw new GamAssetDocumentException("A .gam bundle Smart rule is missing a required field.");
            }

            var ruleKind = kind switch
            {
                "type" => GamAssetDocumentRuleKind.Type,
                "tag" => GamAssetDocumentRuleKind.Tag,
                _ => throw new GamAssetDocumentException("The .gam bundle Smart rule kind is invalid.")
            };
            return new GamAssetDocumentRule(
                ruleKind,
                GamAssetDocumentCodec.CanonicalizeRuleValue(ruleKind, value));
        }

        private static ImageDescriptor ParseImageDescriptor(
            JsonTextReader reader,
            CancellationToken cancellationToken)
        {
            RequireToken(reader, JsonToken.StartObject, "image descriptor");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? path = null;
            string? mediaType = null;
            string? hash = null;
            while (true)
            {
                ReadRequiredToken(reader, cancellationToken, "image descriptor");
                if (reader.TokenType == JsonToken.EndObject)
                {
                    break;
                }

                var property = ReadUniqueProperty(reader, seen, "image descriptor");
                ReadRequiredToken(reader, cancellationToken, property);
                switch (property)
                {
                    case "path":
                        path = ReadString(reader, property);
                        break;
                    case "mediaType":
                        mediaType = ReadString(reader, property);
                        break;
                    case "sha256":
                        hash = ReadString(reader, property);
                        break;
                    default:
                        throw UnsupportedField("image descriptor", property);
                }
            }

            if (path == null || mediaType == null || hash == null)
            {
                throw new GamAssetDocumentException(
                    "A .gam bundle image descriptor is missing a required field.");
            }

            if (!IsSafeArchiveEntryName(path) || !IsImageEntry(path))
            {
                throw new GamAssetDocumentException(
                    "A .gam bundle image descriptor contains an unsafe path.");
            }

            if (!string.Equals(mediaType, "image/png", StringComparison.Ordinal))
            {
                throw new GamAssetDocumentException("Only PNG bundle images are supported.");
            }

            ValidateSha256(hash, "image");
            return new ImageDescriptor(path, hash);
        }

        private static List<string> ParseWorkshopIdArray(
            JsonTextReader reader,
            string fieldName,
            CancellationToken cancellationToken)
        {
            RequireToken(reader, JsonToken.StartArray, fieldName + " array");
            var values = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                ReadRequiredToken(reader, cancellationToken, fieldName + " array");
                if (reader.TokenType == JsonToken.EndArray)
                {
                    return values;
                }

                if (values.Count >= MaximumMembershipAddonIdCount)
                {
                    throw new GamAssetDocumentException(
                        $"The .gam bundle exceeds the {MaximumMembershipAddonIdCount}-addon-ID safety limit.");
                }

                var value = ValidateWorkshopId(ReadString(reader, fieldName));
                if (!seen.Add(value))
                {
                    throw new GamAssetDocumentException(
                        $"The {fieldName} entry contains duplicate addon ID '{value}'.");
                }

                values.Add(value);
            }
        }

        private static List<string> ParsePortableIdArray(
            JsonTextReader reader,
            string fieldName,
            CancellationToken cancellationToken)
        {
            RequireToken(reader, JsonToken.StartArray, fieldName + " array");
            var values = new List<string>();
            while (true)
            {
                ReadRequiredToken(reader, cancellationToken, fieldName + " array");
                if (reader.TokenType == JsonToken.EndArray)
                {
                    return values;
                }

                if (values.Count >= MaximumTopologyReferenceCount)
                {
                    throw new GamAssetDocumentException(
                        $"The .gam bundle exceeds the {MaximumTopologyReferenceCount}-reference safety limit.");
                }

                values.Add(ValidatePortableId(ReadString(reader, fieldName)));
            }
        }

        private static void ValidateParsedResourceCounts(
            IReadOnlyList<ParsedAsset> assets,
            IReadOnlyList<ParsedGroup> groups,
            IReadOnlyList<GamAssetBundleEntryReference> rootChildren)
        {
            long membershipAddonIds = 0;
            long imageCount = 0;
            foreach (var asset in assets)
            {
                membershipAddonIds += asset.Membership.Kind ==
                    GamAssetDocumentMembershipKind.Fixed
                    ? asset.Membership.AddonIds.Count
                    : asset.Membership.SnapshotAddonIds.Count;
                if (membershipAddonIds > MaximumMembershipAddonIdCount)
                {
                    throw new GamAssetDocumentException(
                        $"The .gam bundle exceeds the {MaximumMembershipAddonIdCount}-addon-ID safety limit.");
                }

                if (asset.Image != null)
                {
                    imageCount++;
                }
            }

            long topologyReferences = rootChildren.Count;
            if (topologyReferences > MaximumTopologyReferenceCount)
            {
                throw new GamAssetDocumentException(
                    $"The .gam bundle exceeds the {MaximumTopologyReferenceCount}-reference safety limit.");
            }

            foreach (var group in groups)
            {
                topologyReferences += group.Children.Count;
                if (topologyReferences > MaximumTopologyReferenceCount)
                {
                    throw new GamAssetDocumentException(
                        $"The .gam bundle exceeds the {MaximumTopologyReferenceCount}-reference safety limit.");
                }

                if (group.Image != null)
                {
                    imageCount++;
                }
            }

            if (imageCount > MaximumImageCount)
            {
                throw new GamAssetDocumentException(
                    $"The .gam bundle exceeds the {MaximumImageCount}-image safety limit.");
            }
        }

        private static void ValidateParsedStructure(
            ParsedManifest manifest,
            CancellationToken cancellationToken)
        {
            var assets = manifest.Assets.Select(asset => new GamAssetBundleAsset(
                asset.LocalId,
                asset.Name,
                asset.State,
                asset.Membership,
                imageBytes: null,
                memo: asset.Memo));
            var groups = manifest.Groups.Select(group => new GamAssetBundleGroup(
                group.LocalId,
                group.Name,
                group.DefaultChildState,
                group.Children,
                imageBytes: null,
                memo: group.Memo));
            _ = ValidateForWrite(
                new GamAssetBundleDocument(
                    assets,
                    groups,
                    manifest.RootChildren,
                    manifest.Version),
                cancellationToken);
        }

        private static byte[]? ReadReferencedImage(
            IReadOnlyDictionary<string, ZipArchiveEntry> entries,
            ImageDescriptor? descriptor,
            string expectedPath,
            ISet<string> referencedImages,
            ImageImportBudget imageBudget,
            CancellationToken cancellationToken)
        {
            if (descriptor == null)
            {
                return null;
            }

            if (!string.Equals(descriptor.Path, expectedPath, StringComparison.Ordinal))
            {
                throw new GamAssetDocumentException(
                    $"The .gam bundle image path '{descriptor.Path}' does not match its owner.");
            }

            if (!referencedImages.Add(descriptor.Path))
            {
                throw new GamAssetDocumentException(
                    $"The .gam bundle image entry '{descriptor.Path}' is referenced more than once.");
            }

            if (!entries.TryGetValue(descriptor.Path, out var entry) ||
                !string.Equals(entry.FullName, descriptor.Path, StringComparison.Ordinal))
            {
                throw new GamAssetDocumentException(
                    $"The .gam bundle is missing image entry '{descriptor.Path}'.");
            }

            var encoded = ReadBoundedEntry(
                entry,
                GamAssetDocumentImageNormalizer.MaximumInputBytes,
                cancellationToken);
            var actualHash = ComputeSha256(encoded);
            if (!string.Equals(descriptor.Sha256, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new GamAssetDocumentException(
                    $"The .gam bundle image checksum does not match for '{descriptor.Path}'.");
            }

            var normalized = GamAssetDocumentImageNormalizer.NormalizePortablePng(encoded);
            imageBudget.AddNormalizedBytes(normalized.Length);
            return normalized;
        }

        private static byte[] ReadBoundedEntry(
            ZipArchiveEntry entry,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            if (entry.Length < 0 || entry.Length > maximumBytes)
            {
                throw new GamAssetDocumentException(
                    $"Archive entry '{entry.FullName}' exceeds its {maximumBytes}-byte limit.");
            }

            var declaredLength = checked((int)entry.Length);
            var bytes = new byte[declaredLength];
            using var stream = entry.Open();
            var offset = 0;
            while (offset < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0)
                {
                    throw new GamAssetDocumentException(
                        $"Archive entry '{entry.FullName}' ended before its declared length.");
                }

                offset += read;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (stream.ReadByte() != -1)
            {
                throw new GamAssetDocumentException(
                    $"Archive entry '{entry.FullName}' exceeds its declared length.");
            }

            return bytes;
        }

        private static string ReadUniqueProperty(
            JsonTextReader reader,
            ISet<string> seen,
            string owner)
        {
            RequireToken(reader, JsonToken.PropertyName, owner + " property");
            var property = (string?)reader.Value ?? string.Empty;
            if (!seen.Add(property))
            {
                throw new GamAssetDocumentException(
                    $"The .gam bundle {owner} contains duplicate field '{property}'.");
            }

            return property;
        }

        private static bool ReadToken(
            JsonTextReader reader,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return reader.Read();
        }

        private static void ReadRequiredToken(
            JsonTextReader reader,
            CancellationToken cancellationToken,
            string description)
        {
            if (!ReadToken(reader, cancellationToken))
            {
                throw new GamAssetDocumentException(
                    $"The .gam bundle manifest ended while reading {description}.");
            }
        }

        private static void RequireToken(
            JsonTextReader reader,
            JsonToken expected,
            string description)
        {
            if (reader.TokenType != expected)
            {
                throw new GamAssetDocumentException(
                    $"The .gam bundle {description} has an invalid JSON type.");
            }
        }

        private static string ReadString(JsonTextReader reader, string fieldName)
        {
            RequireToken(reader, JsonToken.String, fieldName);
            return (string?)reader.Value ?? string.Empty;
        }

        private static int ReadInt32(JsonTextReader reader, string fieldName)
        {
            RequireToken(reader, JsonToken.Integer, fieldName);
            try
            {
                return Convert.ToInt32(reader.Value, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (
                ex is FormatException ||
                ex is InvalidCastException ||
                ex is OverflowException)
            {
                throw new GamAssetDocumentException(
                    $"The .gam bundle field '{fieldName}' is outside the supported integer range.",
                    ex);
            }
        }

        private static GamAssetDocumentException UnsupportedField(
            string owner,
            string property)
        {
            return new GamAssetDocumentException(
                $"The .gam bundle {owner} contains unsupported field '{property}'.");
        }

        private static string ValidatePortableId(string value)
        {
            if (string.IsNullOrEmpty(value) ||
                value.Length > MaximumPortableIdLength ||
                !IsAsciiLetterOrDigit(value[0]))
            {
                throw new GamAssetDocumentException(
                    $"Invalid portable local ID '{value ?? string.Empty}'.");
            }

            for (var index = 1; index < value.Length; index++)
            {
                var character = value[index];
                if (!IsAsciiLetterOrDigit(character) &&
                    character != '-' &&
                    character != '_' &&
                    character != '.')
                {
                    throw new GamAssetDocumentException(
                        $"Invalid portable local ID '{value}'.");
                }
            }

            return value;
        }

        private static bool IsAsciiLetterOrDigit(char value)
        {
            return (value >= 'a' && value <= 'z') ||
                (value >= 'A' && value <= 'Z') ||
                (value >= '0' && value <= '9');
        }

        private static string NormalizeAndValidateName(string value, string kind)
        {
            var normalized = value.Trim();
            if (normalized.Length == 0 ||
                normalized.Length > GamAssetDocumentCodec.MaximumAssetNameLength)
            {
                throw new GamAssetDocumentException(
                    $"The {kind} name is empty or too long.");
            }

            if (normalized.Any(char.IsControl))
            {
                throw new GamAssetDocumentException(
                    $"The {kind} name contains control characters.");
            }

            return normalized;
        }

        private static string ValidateWorkshopId(string addonId)
        {
            if (string.IsNullOrEmpty(addonId) ||
                addonId.Length > 20 ||
                addonId[0] == '0')
            {
                throw new GamAssetDocumentException(
                    $"Invalid Workshop addon ID '{addonId ?? string.Empty}'.");
            }

            foreach (var character in addonId)
            {
                if (character < '0' || character > '9')
                {
                    throw new GamAssetDocumentException(
                        $"Invalid Workshop addon ID '{addonId}'.");
                }
            }

            if (!ulong.TryParse(
                    addonId,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsed) ||
                parsed == 0)
            {
                throw new GamAssetDocumentException(
                    $"Invalid Workshop addon ID '{addonId}'.");
            }

            return addonId;
        }

        private static void ValidateState(GamAssetDocumentState state, string owner)
        {
            if (!Enum.IsDefined(typeof(GamAssetDocumentState), state))
            {
                throw new GamAssetDocumentException($"The {owner} state is invalid.");
            }
        }

        private static GamAssetDocumentState ParseState(string value, string owner)
        {
            return value switch
            {
                "enabled" => GamAssetDocumentState.Enabled,
                "disabled" => GamAssetDocumentState.Disabled,
                "excluded" => GamAssetDocumentState.Excluded,
                _ => throw new GamAssetDocumentException($"The {owner} state is invalid.")
            };
        }

        private static string StateToWireValue(GamAssetDocumentState value)
        {
            return value switch
            {
                GamAssetDocumentState.Enabled => "enabled",
                GamAssetDocumentState.Disabled => "disabled",
                GamAssetDocumentState.Excluded => "excluded",
                _ => throw new GamAssetDocumentException("The Asset state is invalid.")
            };
        }

        private static void ValidateSha256(string value, string owner)
        {
            if (value.Length != 64 || value.Any(character =>
                    !((character >= '0' && character <= '9') ||
                      (character >= 'a' && character <= 'f') ||
                      (character >= 'A' && character <= 'F'))))
            {
                throw new GamAssetDocumentException(
                    $"The .gam bundle {owner} SHA-256 value is invalid.");
            }
        }

        private static string ComputeSha256(byte[] value)
        {
            using var sha256 = SHA256.Create();
            return ToHex(sha256.ComputeHash(value));
        }

        private static string ToHex(byte[] hash)
        {
            var builder = new StringBuilder(hash.Length * 2);
            foreach (var value in hash)
            {
                builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private sealed class ImageImportBudget
        {
            private long normalizedBytes;

            public void AddNormalizedBytes(int byteCount)
            {
                if (byteCount < 0 ||
                    normalizedBytes > MaximumAggregateNormalizedImageBytes - byteCount)
                {
                    throw new GamAssetDocumentException(
                        $"The .gam bundle exceeds the {MaximumAggregateNormalizedImageBytes}-byte " +
                        "normalized image safety limit.");
                }

                normalizedBytes += byteCount;
            }
        }

        private sealed class BoundedReadStream : Stream
        {
            private readonly Stream inner;
            private readonly long maximumBytes;
            private readonly string entryName;
            private readonly CancellationToken cancellationToken;
            private long bytesRead;

            public BoundedReadStream(
                Stream inner,
                long maximumBytes,
                string entryName,
                CancellationToken cancellationToken)
            {
                this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
                this.maximumBytes = maximumBytes;
                this.entryName = entryName;
                this.cancellationToken = cancellationToken;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => bytesRead;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (count == 0)
                {
                    return 0;
                }

                if (bytesRead >= maximumBytes)
                {
                    var probe = inner.ReadByte();
                    if (probe != -1)
                    {
                        ThrowLimitExceeded();
                    }

                    return 0;
                }

                var allowed = (int)Math.Min(count, maximumBytes - bytesRead);
                var read = inner.Read(buffer, offset, allowed);
                bytesRead += read;
                return read;
            }

            public override int ReadByte()
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (bytesRead >= maximumBytes)
                {
                    var probe = inner.ReadByte();
                    if (probe != -1)
                    {
                        ThrowLimitExceeded();
                    }

                    return -1;
                }

                var value = inner.ReadByte();
                if (value != -1)
                {
                    bytesRead++;
                }

                return value;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();

            public override void SetLength(long value) =>
                throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();

            private void ThrowLimitExceeded()
            {
                throw new GamAssetDocumentException(
                    $"Archive entry '{entryName}' exceeds its {maximumBytes}-byte limit.");
            }
        }

        private sealed class BoundedWriteStream : Stream
        {
            private readonly Stream inner;
            private readonly long maximumBytes;
            private readonly string entryName;
            private readonly CancellationToken cancellationToken;
            private long bytesWritten;

            public BoundedWriteStream(
                Stream inner,
                long maximumBytes,
                string entryName,
                CancellationToken cancellationToken)
            {
                this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
                this.maximumBytes = maximumBytes;
                this.entryName = entryName;
                this.cancellationToken = cancellationToken;
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => bytesWritten;
            public override long Position
            {
                get => bytesWritten;
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
                cancellationToken.ThrowIfCancellationRequested();
                inner.Flush();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (count < 0 || bytesWritten > maximumBytes - count)
                {
                    throw new GamAssetDocumentException(
                        $"Archive entry '{entryName}' exceeds its {maximumBytes}-byte limit.");
                }

                inner.Write(buffer, offset, count);
                bytesWritten += count;
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();

            public override void SetLength(long value) =>
                throw new NotSupportedException();
        }

        private sealed class ValidatedBundle
        {
            public ValidatedBundle(
                IReadOnlyList<ValidatedAsset> assets,
                IReadOnlyList<ValidatedGroup> groups,
                IReadOnlyList<GamAssetBundleEntryReference> rootChildren)
            {
                Assets = assets;
                Groups = groups;
                RootChildren = rootChildren;
            }

            public IReadOnlyList<ValidatedAsset> Assets { get; }

            public IReadOnlyList<ValidatedGroup> Groups { get; }

            public IReadOnlyList<GamAssetBundleEntryReference> RootChildren { get; }
        }

        private sealed class ValidatedAsset
        {
            public ValidatedAsset(
                GamAssetBundleAsset source,
                string name,
                string? canonicalRuleValue,
                string? memo)
            {
                Source = source;
                Name = name;
                CanonicalRuleValue = canonicalRuleValue;
                Memo = memo;
            }

            public GamAssetBundleAsset Source { get; }

            public string Name { get; }

            public string? CanonicalRuleValue { get; }

            public string? Memo { get; }
        }

        private sealed class ValidatedGroup
        {
            public ValidatedGroup(
                GamAssetBundleGroup source,
                string name,
                string? memo)
            {
                Source = source;
                Name = name;
                Memo = memo;
                Children = Array.Empty<GamAssetBundleEntryReference>();
            }

            public GamAssetBundleGroup Source { get; }

            public string Name { get; }

            public string? Memo { get; }

            public IReadOnlyList<GamAssetBundleEntryReference> Children { get; set; }
        }

        private sealed class ParsedManifest
        {
            public ParsedManifest(
                int version,
                List<ParsedAsset> assets,
                List<ParsedGroup> groups,
                List<GamAssetBundleEntryReference> rootChildren)
            {
                Version = version;
                Assets = assets;
                Groups = groups;
                RootChildren = rootChildren;
            }

            public int Version { get; }

            public List<ParsedAsset> Assets { get; }

            public List<ParsedGroup> Groups { get; }

            public List<GamAssetBundleEntryReference> RootChildren { get; }
        }

        private sealed class ParsedAsset
        {
            public ParsedAsset(
                string localId,
                string name,
                GamAssetDocumentState state,
                GamAssetDocumentMembership membership,
                string? memo,
                bool memoSeen,
                ImageDescriptor? image)
            {
                LocalId = localId;
                Name = name;
                State = state;
                Membership = membership;
                Memo = memo;
                MemoSeen = memoSeen;
                Image = image;
            }

            public string LocalId { get; }

            public string Name { get; }

            public GamAssetDocumentState State { get; }

            public GamAssetDocumentMembership Membership { get; }

            public string? Memo { get; }

            public bool MemoSeen { get; }

            public ImageDescriptor? Image { get; }
        }

        private sealed class ParsedGroup
        {
            public ParsedGroup(
                string localId,
                string name,
                GamAssetDocumentState defaultChildState,
                string? memo,
                bool memoSeen,
                List<string>? legacyChildAssetLocalIds,
                bool legacyChildIdsSeen,
                List<GamAssetBundleEntryReference>? children,
                bool childrenSeen,
                ImageDescriptor? image)
            {
                LocalId = localId;
                Name = name;
                DefaultChildState = defaultChildState;
                Memo = memo;
                MemoSeen = memoSeen;
                LegacyChildAssetLocalIds = legacyChildAssetLocalIds;
                LegacyChildIdsSeen = legacyChildIdsSeen;
                Children = children ?? new List<GamAssetBundleEntryReference>();
                ChildrenSeen = childrenSeen;
                Image = image;
            }

            public string LocalId { get; }

            public string Name { get; }

            public GamAssetDocumentState DefaultChildState { get; }

            public string? Memo { get; }

            public bool MemoSeen { get; }

            public List<string>? LegacyChildAssetLocalIds { get; }

            public bool LegacyChildIdsSeen { get; }

            public List<GamAssetBundleEntryReference> Children { get; set; }

            public bool ChildrenSeen { get; }

            public ImageDescriptor? Image { get; }
        }

        private sealed class ImageDescriptor
        {
            public ImageDescriptor(string path, string sha256)
            {
                Path = path;
                Sha256 = sha256;
            }

            public string Path { get; }

            public string Sha256 { get; }
        }
    }
}
