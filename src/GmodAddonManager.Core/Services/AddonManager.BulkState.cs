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
        private readonly SemaphoreSlim assetStatesGate = new SemaphoreSlim(1, 1);

        public AssetStateChangePreview PreviewAssetStates(IReadOnlyDictionary<string, AddonState> states) =>
            BuildStateChangePreview(GetBatchTargets(states));

        public AssetStateChangePreview PreviewAllOff() => PreviewAssetStates(GetAllOffStates());

        public AssetStateChangePreview PreviewAssetGroupState(string groupId, AddonState state)
        {
            var (children, groups) = AssetGroupService.GetStateScope(configuration, groupId);
            return BuildStateChangePreview(children.ToDictionary(asset => asset, _ => state),
                groups.Select(group => (group.Id, group.DefaultChildState, state)));
        }

        private AssetStateChangePreview BuildStateChangePreview(
            Dictionary<Asset, AddonState> targets,
            IEnumerable<(string Id, AddonState Before, AddonState After)>? groupDefaults = null)
        {
            var assetChanges = targets.Select(pair => (pair.Key.Id, pair.Key.State, pair.Value));
            var groups = groupDefaults ?? Array.Empty<(string, AddonState, AddonState)>();
            var changed = targets.Where(pair => pair.Key.State != pair.Value).ToDictionary(pair => pair.Key, pair => pair.Value);
            if (changed.Count == 0)
                return new AssetStateChangePreview(assetChanges, groups, Array.Empty<string>(), Array.Empty<string>());

            if (!TryGetSubscribedAddonIdSet("PreviewAssetStates", out var subscriptions))
                return new AssetStateChangePreview(assetChanges, groups, null, null);

            if (changed.Keys.All(asset => asset.Id != SubscribeSystemAssetId))
                subscriptions.IntersectWith(changed.Keys.SelectMany(asset => asset.Addons));

            // The resolver needs only identity, current membership and whole-asset state.
            var projected = configuration.Assets.Select(asset => changed.TryGetValue(asset, out var target)
                ? new Asset { Id = asset.Id, Name = asset.Name, IsSystem = asset.IsSystem, Addons = asset.Addons, State = target }
                : asset).ToList();
            var before = BuildExpectedStatesForAssets(configuration.Assets, subscriptions);
            var after = BuildExpectedStatesForAssets(projected, subscriptions);
            return new AssetStateChangePreview(assetChanges, groups,
                after.Where(pair => pair.Value && !before[pair.Key]).Select(pair => pair.Key),
                after.Where(pair => !pair.Value && before[pair.Key]).Select(pair => pair.Key));
        }

        private Dictionary<Asset, AddonState> GetBatchTargets(IReadOnlyDictionary<string, AddonState> states)
        {
            if (states == null) throw new ArgumentNullException(nameof(states));
            var targets = new Dictionary<Asset, AddonState>();
            foreach (var asset in configuration.Assets)
            {
                if ((asset.IsSystem && asset.Id != SubscribeSystemAssetId) || !states.TryGetValue(asset.Id, out var target))
                    continue;
                if (!Enum.IsDefined(typeof(AddonState), target))
                    throw new ArgumentOutOfRangeException(nameof(states), target, "Unknown asset state.");
                targets.Add(asset, target);
            }
            return targets;
        }

        private Dictionary<string, AddonState> GetAllOffStates() => configuration.Assets
            .Where(asset => asset.Id == SubscribeSystemAssetId || (!asset.IsSystem && asset.State == AddonState.Enabled))
            .ToDictionary(asset => asset.Id, _ => AddonState.Disabled, StringComparer.Ordinal);

        public Task ApplyAssetStatesAsync(IReadOnlyDictionary<string, AddonState> states, bool reconcileUnchanged = false) =>
            ApplyBatchAsync(() => states, null, UndoActionType.AssetStatesChanged, reconcileUnchanged);

        public Task<bool> TryApplyAssetStatesAsync(IReadOnlyDictionary<string, AddonState> states, AssetStateChangePreview? expected) =>
            ApplyBatchAsync(() => states, expected, UndoActionType.AssetStatesChanged, reconcileUnchanged: false);

        public Task<bool> TrySetAllOffAsync(AssetStateChangePreview? expected) =>
            ApplyBatchAsync(GetAllOffStates, expected, UndoActionType.AllOff, reconcileUnchanged: false);

        private async Task<bool> ApplyBatchAsync(
            Func<IReadOnlyDictionary<string, AddonState>> getStates,
            AssetStateChangePreview? expected,
            UndoActionType actionType,
            bool reconcileUnchanged)
        {
            await assetStatesGate.WaitAsync();
            try
            {
                var targets = GetBatchTargets(getStates());
                if (expected != null && !expected.Matches(BuildStateChangePreview(targets))) return false;

                var changed = targets.Where(pair => pair.Key.State != pair.Value).ToList();
                if (changed.Count == 0)
                {
                    if (reconcileUnchanged) await UpdateAddonStatesAsync();
                    return true;
                }

                var previous = changed.ToDictionary(pair => pair.Key.Id, pair => pair.Key.State, StringComparer.Ordinal);
                foreach (var pair in changed) pair.Key.SetWholeState(pair.Value);
                try
                {
                    await SaveConfigurationImmediatelyAsync();
                }
                catch
                {
                    foreach (var pair in changed) pair.Key.SetWholeState(previous[pair.Key.Id]);
                    throw;
                }

                undoManager.RecordAction(new UndoAction(actionType, actionType == UndoActionType.AllOff
                    ? "Turned all addon sources off" : "Changed asset states") { PreviousAssetStates = previous });
                try
                {
                    await UpdateAddonStatesAsync();
                }
                catch (Exception ex)
                {
                    QueueRuntimeApplyOrRemember("ApplyAssetStatesAsync");
                    errorHandler.HandleError(ex, "Asset states were saved; runtime reconciliation was queued", ErrorSeverity.Warning);
                }
                return true;
            }
            finally
            {
                assetStatesGate.Release();
            }
        }

        public async Task<bool> TryApplyAssetGroupStateAsync(string groupId, AddonState state, AssetStateChangePreview? expected)
        {
            await assetStatesGate.WaitAsync();
            try
            {
                if (expected != null && !expected.Matches(PreviewAssetGroupState(groupId, state))) return false;
                var rollback = CloneConfiguration(configuration);
                var action = assetGroupService.ApplyGroupState(configuration, groupId, state);
                await CommitAssetGroupMutationAsync(action, rollback, action != null, "Asset Group state change");
                return true;
            }
            finally
            {
                assetStatesGate.Release();
            }
        }
    }
}
