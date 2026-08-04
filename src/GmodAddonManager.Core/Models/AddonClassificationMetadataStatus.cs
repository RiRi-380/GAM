using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace GmodAddonManager.Core.Models
{
    /// <summary>
    /// Distinguishes a confirmed empty/non-matching classification from a
    /// transiently unavailable metadata source. Unknown must never evict an
    /// existing Smart Asset member.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum AddonClassificationMetadataStatus
    {
        Unknown,
        Known
    }
}
