using System;
using System.Collections.Generic;

namespace GmodAddonManager.Core.Models
{
    public sealed class AddonStateSnapshot
    {
        public AddonStateSnapshot(
            IReadOnlyDictionary<string, bool> states,
            string normalizedState,
            DateTime capturedAtUtc,
            string? source)
        {
            States = states ?? throw new ArgumentNullException(nameof(states));
            NormalizedState = normalizedState ?? throw new ArgumentNullException(nameof(normalizedState));
            CapturedAtUtc = capturedAtUtc;
            Source = source;
        }

        public DateTime CapturedAtUtc { get; }
        public IReadOnlyDictionary<string, bool> States { get; }
        public string NormalizedState { get; }
        public string? Source { get; }
    }
}
