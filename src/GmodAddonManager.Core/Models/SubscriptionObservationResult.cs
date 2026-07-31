using System.Collections.Generic;

namespace GmodAddonManager.Core.Models
{
    public sealed class SubscriptionObservationResult
    {
        public bool Changed { get; set; }

        public bool IsAuthoritative { get; set; }

        public IReadOnlyList<string> NewlySubscribedIds { get; set; } = new List<string>();

        public IReadOnlyList<string> UnsubscribedIds { get; set; } = new List<string>();

        public int PendingDownloadCount { get; set; }
    }
}
