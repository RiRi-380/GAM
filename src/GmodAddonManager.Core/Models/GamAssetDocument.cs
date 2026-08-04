using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace GmodAddonManager.Core.Models
{
    /// <summary>
    /// Portable, application-independent representation of one exported GAM asset.
    /// It deliberately excludes configuration identity, favorites, version history,
    /// local paths, and runtime state.
    /// </summary>
    public sealed class GamAssetDocument
    {
        private readonly byte[]? imageBytes;

        public GamAssetDocument(
            string name,
            GamAssetDocumentState state,
            GamAssetDocumentMembership membership,
            byte[]? imageBytes = null,
            int sourceFormatVersion = 3,
            string? memo = null)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            State = state;
            Membership = membership ?? throw new ArgumentNullException(nameof(membership));
            this.imageBytes = imageBytes == null ? null : (byte[])imageBytes.Clone();
            SourceFormatVersion = sourceFormatVersion;
            Memo = memo;
        }

        public string Name { get; }

        public GamAssetDocumentState State { get; }

        public GamAssetDocumentMembership Membership { get; }

        /// <summary>
        /// Optional user-authored portable notes. The codec canonicalizes line
        /// endings and validates the bounded text before writing or returning a
        /// document read from disk.
        /// </summary>
        public string? Memo { get; }

        /// <summary>
        /// Image data supplied by the caller or decoded from the document. The codec
        /// normalizes this to a bounded 512 x 512 PNG before it crosses the file boundary.
        /// </summary>
        public byte[]? ImageBytes => imageBytes == null ? null : (byte[])imageBytes.Clone();

        /// <summary>
        /// Version read from disk. New documents normally use version 3; legacy text
        /// documents are reported as version 1 and JSON documents without Memo as v2.
        /// </summary>
        public int SourceFormatVersion { get; }
    }

    public enum GamAssetDocumentState
    {
        Enabled = 0,
        Disabled = 1,
        Excluded = 2
    }

    public enum GamAssetDocumentMembershipKind
    {
        Fixed = 0,
        Smart = 1
    }

    public enum GamAssetDocumentRuleKind
    {
        Type = 0,
        Tag = 1
    }

    public sealed class GamAssetDocumentRule
    {
        public GamAssetDocumentRule(GamAssetDocumentRuleKind kind, string value)
        {
            Kind = kind;
            Value = value ?? throw new ArgumentNullException(nameof(value));
        }

        public GamAssetDocumentRuleKind Kind { get; }

        public string Value { get; }
    }

    public sealed class GamAssetDocumentMembership
    {
        private GamAssetDocumentMembership(
            GamAssetDocumentMembershipKind kind,
            IEnumerable<string> addonIds,
            GamAssetDocumentRule? rule,
            IEnumerable<string> snapshotAddonIds)
        {
            Kind = kind;
            AddonIds = Copy(addonIds, nameof(addonIds));
            Rule = rule;
            SnapshotAddonIds = Copy(snapshotAddonIds, nameof(snapshotAddonIds));
        }

        public GamAssetDocumentMembershipKind Kind { get; }

        /// <summary>
        /// Authoritative membership for a fixed asset. Empty for smart assets.
        /// </summary>
        public IReadOnlyList<string> AddonIds { get; }

        /// <summary>
        /// Authoritative rule for a smart asset. Null for fixed assets.
        /// </summary>
        public GamAssetDocumentRule? Rule { get; }

        /// <summary>
        /// Informational export-time membership for a smart asset. It is not the rule
        /// authority and may be stale when the document is imported.
        /// </summary>
        public IReadOnlyList<string> SnapshotAddonIds { get; }

        public static GamAssetDocumentMembership Fixed(IEnumerable<string> addonIds)
        {
            return new GamAssetDocumentMembership(
                GamAssetDocumentMembershipKind.Fixed,
                addonIds,
                null,
                Array.Empty<string>());
        }

        public static GamAssetDocumentMembership Smart(
            GamAssetDocumentRule rule,
            IEnumerable<string>? snapshotAddonIds = null)
        {
            return new GamAssetDocumentMembership(
                GamAssetDocumentMembershipKind.Smart,
                Array.Empty<string>(),
                rule ?? throw new ArgumentNullException(nameof(rule)),
                snapshotAddonIds ?? Array.Empty<string>());
        }

        private static IReadOnlyList<string> Copy(IEnumerable<string> values, string parameterName)
        {
            if (values == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            return new ReadOnlyCollection<string>(values.ToList());
        }
    }

    public sealed class GamAssetDocumentException : Exception
    {
        public GamAssetDocumentException(string message)
            : base(message)
        {
        }

        public GamAssetDocumentException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
