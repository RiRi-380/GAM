using System;
using System.Collections.Generic;

namespace GmodAddonManager.Core.Models
{
    public sealed class AssetVersionMembershipDiffResult
    {
        public AssetVersionMembershipDiffResult(
            int version,
            IReadOnlyList<string> currentOnlyIds,
            IReadOnlyList<string> snapshotOnlyIds)
        {
            Version = version;
            CurrentOnlyIds = currentOnlyIds ?? Array.Empty<string>();
            SnapshotOnlyIds = snapshotOnlyIds ?? Array.Empty<string>();
        }

        public int Version { get; }

        /// <summary>
        /// IDs currently in the Asset but absent from the snapshot.
        /// </summary>
        public IReadOnlyList<string> CurrentOnlyIds { get; }

        /// <summary>
        /// IDs stored in the snapshot but absent from the current Asset.
        /// </summary>
        public IReadOnlyList<string> SnapshotOnlyIds { get; }

        public bool HasChanges => CurrentOnlyIds.Count > 0 || SnapshotOnlyIds.Count > 0;
    }
}
