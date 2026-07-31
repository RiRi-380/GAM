using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace GmodAddonManager.Core.Models
{
    public sealed class AddonMountSnapshot
    {
        [JsonProperty("path")]
        public string Path { get; set; } = string.Empty;

        [JsonProperty("disabledIds")]
        public List<string> DisabledIds { get; set; } = new List<string>();

        [JsonProperty("semanticHash")]
        public string SemanticHash { get; set; } = string.Empty;

        [JsonProperty("physicalHash")]
        public string PhysicalHash { get; set; } = string.Empty;

        [JsonProperty("fileLastWriteUtc")]
        public DateTime? FileLastWriteUtc { get; set; }

        [JsonProperty("fileSize")]
        public long? FileSize { get; set; }

        [JsonProperty("fileExists")]
        public bool FileExists { get; set; }

        [JsonProperty("isValidFormat")]
        public bool IsValidFormat { get; set; }

        [JsonProperty("observedAtUtc")]
        public DateTime ObservedAtUtc { get; set; } = DateTime.UtcNow;
    }

}
