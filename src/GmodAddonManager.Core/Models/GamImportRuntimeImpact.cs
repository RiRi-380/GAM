using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace GmodAddonManager.Core.Models
{
    public sealed class GamImportRuntimeImpact
    {
        internal GamImportRuntimeImpact(bool isKnown, IDictionary<string, bool> changes)
        {
            IsKnown = isKnown;
            Changes = new ReadOnlyDictionary<string, bool>(
                new Dictionary<string, bool>(changes, System.StringComparer.Ordinal));
        }

        public bool IsKnown { get; }
        public IReadOnlyDictionary<string, bool> Changes { get; }

        internal bool Matches(GamImportRuntimeImpact other) =>
            IsKnown == other.IsKnown &&
            Changes.Count == other.Changes.Count &&
            Changes.All(pair => other.Changes.TryGetValue(pair.Key, out var value) &&
                value == pair.Value);
    }
}
