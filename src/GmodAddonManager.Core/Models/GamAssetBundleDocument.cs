using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace GmodAddonManager.Core.Models
{
    /// <summary>
    /// Portable representation of one or more Assets and nested Asset Groups.
    /// Local IDs exist only inside the bundle and must be replaced with fresh
    /// configuration IDs by the importer.
    /// </summary>
    public sealed class GamAssetBundleDocument
    {
        public GamAssetBundleDocument(
            IEnumerable<GamAssetBundleAsset> assets,
            IEnumerable<GamAssetBundleGroup> groups,
            int sourceFormatVersion = 4)
        {
            Assets = Copy(assets, nameof(assets));
            Groups = Copy(groups, nameof(groups));
            RootChildren = Copy(
                BuildImplicitRootChildren(Assets, Groups),
                nameof(RootChildren));
            SourceFormatVersion = sourceFormatVersion;
        }

        public GamAssetBundleDocument(
            IEnumerable<GamAssetBundleAsset> assets,
            IEnumerable<GamAssetBundleGroup> groups,
            IEnumerable<GamAssetBundleEntryReference> rootChildren,
            int sourceFormatVersion = 4)
        {
            Assets = Copy(assets, nameof(assets));
            Groups = Copy(groups, nameof(groups));
            RootChildren = Copy(rootChildren, nameof(rootChildren));
            SourceFormatVersion = sourceFormatVersion;
        }

        public IReadOnlyList<GamAssetBundleAsset> Assets { get; }

        public IReadOnlyList<GamAssetBundleGroup> Groups { get; }

        /// <summary>
        /// Exact mixed Asset/Group order directly below the bundle root.
        /// Every portable entity must occur exactly once in this topology.
        /// </summary>
        public IReadOnlyList<GamAssetBundleEntryReference> RootChildren { get; }

        public int SourceFormatVersion { get; }

        private static IReadOnlyList<T> Copy<T>(IEnumerable<T> values, string parameterName)
        {
            if (values == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            return new ReadOnlyCollection<T>(values.ToList());
        }

        private static IEnumerable<GamAssetBundleEntryReference> BuildImplicitRootChildren(
            IEnumerable<GamAssetBundleAsset> assets,
            IEnumerable<GamAssetBundleGroup> groups)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }
            if (groups == null)
            {
                throw new ArgumentNullException(nameof(groups));
            }

            var assetList = assets.ToList();
            var groupList = groups.ToList();
            var nestedIds = new HashSet<string>(
                groupList.SelectMany(group => group?.Children ??
                    Array.Empty<GamAssetBundleEntryReference>())
                    .Where(child => child != null)
                    .Select(child => child.LocalId),
                StringComparer.OrdinalIgnoreCase);

            return assetList
                .Where(asset => asset != null && !nestedIds.Contains(asset.LocalId))
                .Select(asset => GamAssetBundleEntryReference.Asset(asset.LocalId))
                .Concat(groupList
                    .Where(group => group != null && !nestedIds.Contains(group.LocalId))
                    .Select(group => GamAssetBundleEntryReference.Group(group.LocalId)))
                .ToArray();
        }
    }

    public sealed class GamAssetBundleAsset
    {
        private readonly byte[]? imageBytes;

        public GamAssetBundleAsset(
            string localId,
            string name,
            GamAssetDocumentState state,
            GamAssetDocumentMembership membership,
            byte[]? imageBytes = null,
            string? memo = null)
        {
            LocalId = localId ?? throw new ArgumentNullException(nameof(localId));
            Name = name ?? throw new ArgumentNullException(nameof(name));
            State = state;
            Membership = membership ?? throw new ArgumentNullException(nameof(membership));
            this.imageBytes = imageBytes == null ? null : (byte[])imageBytes.Clone();
            Memo = memo;
        }

        public string LocalId { get; }

        public string Name { get; }

        public GamAssetDocumentState State { get; }

        public GamAssetDocumentMembership Membership { get; }

        public string? Memo { get; }

        public byte[]? ImageBytes => imageBytes == null ? null : (byte[])imageBytes.Clone();
    }

    public sealed class GamAssetBundleGroup
    {
        private readonly byte[]? imageBytes;

        public GamAssetBundleGroup(
            string localId,
            string name,
            GamAssetDocumentState defaultChildState,
            IEnumerable<string> childAssetLocalIds,
            byte[]? imageBytes = null,
            string? memo = null)
            : this(
                localId,
                name,
                defaultChildState,
                ConvertLegacyChildren(childAssetLocalIds),
                imageBytes,
                memo)
        {
        }

        public GamAssetBundleGroup(
            string localId,
            string name,
            GamAssetDocumentState defaultChildState,
            IEnumerable<GamAssetBundleEntryReference> children,
            byte[]? imageBytes = null,
            string? memo = null)
        {
            LocalId = localId ?? throw new ArgumentNullException(nameof(localId));
            Name = name ?? throw new ArgumentNullException(nameof(name));
            DefaultChildState = defaultChildState;
            Children = Copy(children, nameof(children));
            this.imageBytes = imageBytes == null ? null : (byte[])imageBytes.Clone();
            Memo = memo;
        }

        public string LocalId { get; }

        public string Name { get; }

        /// <summary>
        /// Last/default state inherited by Assets created inside this Group.
        /// Child Asset states remain independently portable.
        /// </summary>
        public GamAssetDocumentState DefaultChildState { get; }

        /// <summary>
        /// Exact mixed Asset/Group order directly below this Group.
        /// </summary>
        public IReadOnlyList<GamAssetBundleEntryReference> Children { get; }

        /// <summary>
        /// Compatibility projection for v3 callers. Nested Group references are
        /// deliberately omitted; new code must use <see cref="Children"/>.
        /// </summary>
        public IReadOnlyList<string> ChildAssetLocalIds => new ReadOnlyCollection<string>(
            Children
                .Where(child => child.Kind == GamAssetBundleEntryKind.Asset)
                .Select(child => child.LocalId)
                .ToList());

        public string? Memo { get; }

        public byte[]? ImageBytes => imageBytes == null ? null : (byte[])imageBytes.Clone();

        private static IEnumerable<GamAssetBundleEntryReference> ConvertLegacyChildren(
            IEnumerable<string> values)
        {
            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            return values.Select(GamAssetBundleEntryReference.Asset).ToArray();
        }

        private static IReadOnlyList<T> Copy<T>(
            IEnumerable<T> values,
            string parameterName)
        {
            if (values == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            return new ReadOnlyCollection<T>(values.ToList());
        }
    }

    public enum GamAssetBundleEntryKind
    {
        Asset = 0,
        Group = 1
    }

    /// <summary>
    /// A portable topology edge. Local IDs are resolved against the bundle's
    /// Asset/Group tables and are validated by the codec.
    /// </summary>
    public sealed class GamAssetBundleEntryReference
    {
        public GamAssetBundleEntryReference(
            GamAssetBundleEntryKind kind,
            string localId)
        {
            Kind = kind;
            LocalId = localId ?? throw new ArgumentNullException(nameof(localId));
        }

        public GamAssetBundleEntryKind Kind { get; }

        public string LocalId { get; }

        public static GamAssetBundleEntryReference Asset(string localId)
        {
            return new GamAssetBundleEntryReference(GamAssetBundleEntryKind.Asset, localId);
        }

        public static GamAssetBundleEntryReference Group(string localId)
        {
            return new GamAssetBundleEntryReference(GamAssetBundleEntryKind.Group, localId);
        }
    }
}
