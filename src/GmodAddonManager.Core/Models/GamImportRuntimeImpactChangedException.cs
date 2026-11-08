using System;

namespace GmodAddonManager.Core.Models
{
    public sealed class GamImportRuntimeImpactChangedException : InvalidOperationException
    {
        public GamImportRuntimeImpactChangedException()
            : base("The GMod state or expected import changes changed after the preview.")
        {
        }
    }
}
