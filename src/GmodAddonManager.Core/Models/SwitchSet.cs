using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Newtonsoft.Json;

namespace GmodAddonManager.Core.Models
{
    public sealed class SwitchSet
    {
        [JsonProperty("id")]
        public string Id { get; }

        [JsonProperty("name")]
        public string Name { get; }

        [JsonProperty("assetStates")]
        public IReadOnlyDictionary<string, AddonState> AssetStates { get; }

        [JsonConstructor]
        public SwitchSet(string id, string name, IReadOnlyDictionary<string, AddonState> assetStates)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || assetStates == null)
            {
                throw new ArgumentException("A switch set needs an ID, a name, and asset states.");
            }
            if (assetStates.Any(pair => string.IsNullOrWhiteSpace(pair.Key) ||
                !Enum.IsDefined(typeof(AddonState), pair.Value)))
            {
                throw new ArgumentException("Invalid saved asset state.", nameof(assetStates));
            }

            Id = id;
            Name = name.Trim();
            AssetStates = new ReadOnlyDictionary<string, AddonState>(
                assetStates.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        }
    }
}
