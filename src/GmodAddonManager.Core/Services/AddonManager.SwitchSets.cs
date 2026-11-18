using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GmodAddonManager.Core.Models;

namespace GmodAddonManager.Core.Services
{
    public partial class AddonManager
    {
        private readonly SemaphoreSlim switchSetGate = new SemaphoreSlim(1, 1);

        private IEnumerable<Asset> SwitchableAssets =>
            configuration.Assets.Where(asset => !asset.IsSystem || asset.Id == SubscribeSystemAssetId);

        public async Task<SwitchSet> SaveSwitchSetAsync(string name)
        {
            var trimmed = CheckSwitchSetName(name);
            return await EditSwitchSetsAsync(sets =>
            {
                EnsureUniqueSwitchSetName(sets, trimmed, null);
                var states = SwitchableAssets.ToDictionary(asset => asset.Id, asset => asset.GetWholeState(), StringComparer.Ordinal);
                var set = new SwitchSet(Guid.NewGuid().ToString("N"), trimmed, states);
                sets.Add(set);
                return set;
            });
        }

        public async Task RenameSwitchSetAsync(string id, string name)
        {
            var trimmed = CheckSwitchSetName(name);
            await EditSwitchSetsAsync(sets =>
            {
                var index = FindSwitchSetIndex(sets, id);
                EnsureUniqueSwitchSetName(sets, trimmed, id);
                var renamed = new SwitchSet(id, trimmed, sets[index].AssetStates);
                sets[index] = renamed;
                return renamed;
            });
        }

        public async Task DeleteSwitchSetAsync(string id)
        {
            await EditSwitchSetsAsync(sets =>
            {
                var index = FindSwitchSetIndex(sets, id);
                var deleted = sets[index];
                sets.RemoveAt(index);
                return deleted;
            });
        }

        public async Task ApplySwitchSetAsync(string id)
        {
            await switchSetGate.WaitAsync();
            try
            {
                var set = configuration.SwitchSets[FindSwitchSetIndex(configuration.SwitchSets, id)];
                await ApplyAssetStatesAsync(set.AssetStates, reconcileUnchanged: true);
            }
            finally
            {
                switchSetGate.Release();
            }
        }

        public (int Enabled, int Disabled, int Excluded, int NewAssets, int RemovedAssets) GetSwitchSetChanges(SwitchSet set)
        {
            var current = SwitchableAssets.ToDictionary(asset => asset.Id, asset => asset.GetWholeState(), StringComparer.Ordinal);
            var changes = set.AssetStates.Where(pair => current.TryGetValue(pair.Key, out var state) && state != pair.Value).ToList();
            return (
                changes.Count(pair => pair.Value == AddonState.Enabled),
                changes.Count(pair => pair.Value == AddonState.Disabled),
                changes.Count(pair => pair.Value == AddonState.Excluded),
                current.Keys.Count(id => !set.AssetStates.ContainsKey(id)),
                set.AssetStates.Keys.Count(id => !current.ContainsKey(id)));
        }

        private static string CheckSwitchSetName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A switch set needs a name.", nameof(name));
            }
            return name.Trim();
        }

        private static void EnsureUniqueSwitchSetName(List<SwitchSet> sets, string name, string? exceptId)
        {
            if (sets.Any(set => set.Id != exceptId && string.Equals(set.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("A switch set with this name already exists.", nameof(name));
            }
        }

        private static int FindSwitchSetIndex(List<SwitchSet> sets, string id)
        {
            var index = sets.FindIndex(set => set.Id == id);
            return index >= 0 ? index : throw new KeyNotFoundException("The switch set no longer exists.");
        }

        private async Task<SwitchSet> EditSwitchSetsAsync(Func<List<SwitchSet>, SwitchSet> edit)
        {
            await switchSetGate.WaitAsync();
            try
            {
                var previous = configuration.SwitchSets;
                var next = new List<SwitchSet>(previous);
                var result = edit(next);
                configuration.SwitchSets = next;
                try
                {
                    await SaveConfigurationImmediatelyAsync();
                }
                catch
                {
                    configuration.SwitchSets = previous;
                    throw;
                }
                return result;
            }
            finally
            {
                switchSetGate.Release();
            }
        }
    }
}
