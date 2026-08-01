using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GmodAddonManager.Core.Models;
using Newtonsoft.Json;

namespace GmodAddonManager.Core.Services
{
    internal enum PendingChangeActionType
    {
        Unknown,
        ApplyStates
    }

    /// <summary>
    /// GMod実行中の変更を「最新構成を全再適用する」一つのmarkerへ畳み込む。
    /// 古い個別操作は再生しない。
    /// </summary>
    public sealed class PendingChangeManager
    {
        private const string ApplyStatesAction = "apply_states";
        private const string ApplyStatesTarget = "*";

        private readonly string pendingPath;
        private readonly string pendingBackupPath;
        private readonly AddonManager addonManager;
        private readonly IErrorHandler? errorHandler;
        private readonly object lockObject = new object();
        private readonly SemaphoreSlim applyGate = new SemaphoreSlim(1, 1);
        private PendingChanges pendingChanges = new PendingChanges();
        private Guid markerGeneration = Guid.Empty;
        private bool pendingStateReadWasAuthoritative;

        internal Func<Task>? BeforeRuntimeApplyAsync { get; set; }

        public event EventHandler<ChangeAppliedEventArgs>? ChangeApplied;
        public event EventHandler<ChangeFailedEventArgs>? ChangeFailed;

        public PendingChangeManager(
            AddonManager addonManager,
            string managerPath,
            IErrorHandler? errorHandler = null)
        {
            this.addonManager = addonManager ?? throw new ArgumentNullException(nameof(addonManager));
            pendingPath = Path.Combine(managerPath, "pending.json");
            pendingBackupPath = Path.Combine(managerPath, "pending.json.bak");
            this.errorHandler = errorHandler;
            LoadPendingChanges();

            addonManager.PendingChangeCountProvider = () => GetPendingChangeCount();
            addonManager.QueueRuntimeApplyTrackedProvider = QueueApplyStatesTracked;
            addonManager.ClearRuntimeApplyIfGenerationProvider =
                TryClearApplyMarkerIfGeneration;
            addonManager.QueueRuntimeApplyProvider = QueueApplyStates;
            try
            {
                addonManager.TryFinalizeOrphanedRuntimeAttributionConflict(
                    HasDurablyConfirmedNoPendingMarker());
            }
            catch (Exception ex)
            {
                errorHandler?.HandleWarning(
                    $"An orphaned runtime-attribution conflict could not be finalized: {ex.Message}",
                    "PendingChangeManager.Initialize");
            }
        }

        public void QueueChange(AddonChange change)
        {
            if (change != null)
            {
                QueueApplyStates();
            }
        }

        public void QueueChanges(IEnumerable<AddonChange> changes)
        {
            if (changes != null && changes.Any(change => change != null))
            {
                QueueApplyStates();
            }
        }

        public void AddPendingChange(string action, string assetId)
        {
            _ = action;
            _ = assetId;
            QueueApplyStates();
        }

        public void QueueAssetToggle(string assetId, bool enable)
        {
            _ = assetId;
            _ = enable;
            QueueApplyStates();
        }

        public void QueueApplyStates()
        {
            _ = QueueApplyStatesTracked();
        }

        internal Guid QueueApplyStatesTracked()
        {
            lock (lockObject)
            {
                var previousChanges = pendingChanges.Changes.ToList();
                var previousGeneration = markerGeneration;
                ReplaceWithApplyMarkerNoLock(DateTime.UtcNow);
                if (!TrySavePendingChangesNoLock())
                {
                    pendingChanges.Changes = previousChanges;
                    markerGeneration = previousGeneration;
                    throw new IOException(
                        "The pending runtime apply marker could not be persisted.");
                }

                return markerGeneration;
            }
        }

        internal bool TryClearApplyMarkerIfGeneration(Guid expectedGeneration)
        {
            lock (lockObject)
            {
                if (markerGeneration != expectedGeneration)
                {
                    return false;
                }

                var previousChanges = pendingChanges.Changes.ToList();
                pendingChanges.Changes.RemoveAll(
                    change => ParseActionType(change.Action) ==
                              PendingChangeActionType.ApplyStates);
                if (!TrySavePendingChangesNoLock())
                {
                    pendingChanges.Changes = previousChanges;
                    throw new IOException(
                        "The pending runtime apply marker could not be durably cleared.");
                }

                return true;
            }
        }

        internal static PendingChangeActionType ParseActionType(string? action)
        {
            return string.Equals(
                    action?.Trim(),
                    ApplyStatesAction,
                    StringComparison.OrdinalIgnoreCase)
                ? PendingChangeActionType.ApplyStates
                : PendingChangeActionType.Unknown;
        }

        public async Task ApplyPendingChangesAsync()
        {
            await applyGate.WaitAsync();
            try
            {
                await ApplyPendingChangesCoreAsync();
            }
            finally
            {
                applyGate.Release();
            }
        }

        private async Task ApplyPendingChangesCoreAsync()
        {
            AddonChange? marker;
            Guid capturedMarkerGeneration;
            lock (lockObject)
            {
                marker = pendingChanges.Changes
                    .Where(change => ParseActionType(change.Action) == PendingChangeActionType.ApplyStates)
                    .OrderByDescending(change => change.Timestamp)
                    .FirstOrDefault();
                capturedMarkerGeneration = markerGeneration;
            }

            if (marker == null)
            {
                return;
            }

            if (addonManager.IsGmodCurrentlyRunning())
            {
                return;
            }

            try
            {
                if (BeforeRuntimeApplyAsync != null)
                {
                    await BeforeRuntimeApplyAsync();
                }

                var pendingObservation =
                    await addonManager.RefreshGmodDisabledAddonsBeforePendingApplyAsync();
                if (pendingObservation?.PendingRecovery ==
                    PendingGamRuntimeWriteRecovery.Conflicted)
                {
                    var markerCleared = TryClearApplyMarkerIfGeneration(
                        capturedMarkerGeneration);
                    if (markerCleared)
                    {
                        await addonManager.FinalizeRuntimeAttributionConflictAsync(
                            pendingObservation.PendingOperationId);
                    }

                    var conflictError = new InvalidOperationException(
                        "Automatic runtime apply was cancelled because GMod changed while a prior GAM write was unresolved.");
                    ChangeFailed?.Invoke(this, new ChangeFailedEventArgs
                    {
                        Change = marker,
                        Error = conflictError
                    });
                    errorHandler?.HandleWarning(
                        conflictError.Message,
                        "PendingChangeManager.ApplyPendingChangesAsync");
                    return;
                }

                var applyResult = await addonManager.UpdateAddonStatesWithResultAsync();
                if (!applyResult.Succeeded)
                {
                    if (string.Equals(
                            applyResult.FailureCode,
                            AddonManager.RuntimeAttributionConflictFailureCode,
                            StringComparison.Ordinal))
                    {
                        var markerCleared = TryClearApplyMarkerIfGeneration(
                            capturedMarkerGeneration);
                        if (markerCleared)
                        {
                            await addonManager.FinalizeRuntimeAttributionConflictAsync(
                                applyResult.AttributionConflictOperationId);
                        }

                        var conflictError = new InvalidOperationException(
                            "Automatic runtime apply was cancelled because GMod changed while a prior GAM write was unresolved.");
                        ChangeFailed?.Invoke(this, new ChangeFailedEventArgs
                        {
                            Change = marker,
                            Error = conflictError
                        });
                        errorHandler?.HandleWarning(
                            conflictError.Message,
                            "PendingChangeManager.ApplyPendingChangesAsync");
                        return;
                    }

                    var applyError = new InvalidOperationException(
                        "The latest desired addon state could not be reconciled.");
                    ChangeFailed?.Invoke(this, new ChangeFailedEventArgs
                    {
                        Change = marker,
                        Error = applyError
                    });
                    errorHandler?.HandleWarning(
                        applyError.Message,
                        "PendingChangeManager.ApplyPendingChangesAsync");
                    return;
                }

                if (addonManager.IsGmodCurrentlyRunning())
                {
                    return;
                }

                lock (lockObject)
                {
                    // A user operation may queue a replacement marker while the
                    // previous generation is being applied. Only clear the exact
                    // generation captured above; the replacement represents newer
                    // desired state and must survive for another full reconcile.
                    if (markerGeneration == capturedMarkerGeneration)
                    {
                        var previousChanges = pendingChanges.Changes.ToList();
                        pendingChanges.Changes.RemoveAll(
                            change => ParseActionType(change.Action) == PendingChangeActionType.ApplyStates);
                        if (!TrySavePendingChangesNoLock())
                        {
                            pendingChanges.Changes = previousChanges;
                            throw new IOException(
                                "The applied pending marker could not be durably cleared.");
                        }
                    }
                }

                ChangeApplied?.Invoke(this, new ChangeAppliedEventArgs { Change = marker });
            }
            catch (Exception ex)
            {
                ChangeFailed?.Invoke(this, new ChangeFailedEventArgs
                {
                    Change = marker,
                    Error = ex
                });
                errorHandler?.HandleError(
                    ex,
                    "PendingChangeManager.ApplyPendingChangesAsync",
                    ErrorSeverity.Warning);
            }
        }

        public bool HasPendingChanges()
        {
            lock (lockObject)
            {
                return pendingChanges.Changes.Any(
                    change => ParseActionType(change.Action) == PendingChangeActionType.ApplyStates);
            }
        }

        public int GetPendingChangeCount()
        {
            return HasPendingChanges() ? 1 : 0;
        }

        public List<AddonChange> GetPendingChanges()
        {
            lock (lockObject)
            {
                return pendingChanges.Changes
                    .Where(change => ParseActionType(change.Action) == PendingChangeActionType.ApplyStates)
                    .Take(1)
                    .ToList();
            }
        }

        public void ClearPendingChanges()
        {
            lock (lockObject)
            {
                pendingChanges.Changes.Clear();
                markerGeneration = Guid.NewGuid();
                TryDeletePendingFile(pendingPath);
                TryDeletePendingFile(pendingBackupPath);
            }
        }

        private void LoadPendingChanges()
        {
            lock (lockObject)
            {
                var loadedChanges = new List<AddonChange>();
                var loadedAny = false;
                var primaryExists = File.Exists(pendingPath);
                var primaryReadable =
                    TryReadPendingChanges(pendingPath, out var primary);
                if (primaryReadable)
                {
                    loadedAny = true;
                    loadedChanges.AddRange(primary.Changes);
                }

                var backupExists = File.Exists(pendingBackupPath);
                var backupReadable =
                    TryReadPendingChanges(pendingBackupPath, out var backup);
                if (backupReadable)
                {
                    loadedAny = true;
                    loadedChanges.AddRange(backup.Changes);
                }

                pendingStateReadWasAuthoritative =
                    (!primaryExists || primaryReadable) &&
                    (!backupExists || backupReadable);

                pendingChanges = new PendingChanges();
                if (!loadedAny || loadedChanges.Count == 0)
                {
                    return;
                }

                ReplaceWithApplyMarkerNoLock(
                    loadedChanges.Max(change => change.Timestamp));
                if (TrySavePendingChangesNoLock())
                {
                    pendingStateReadWasAuthoritative = true;
                }
            }
        }

        private bool HasDurablyConfirmedNoPendingMarker()
        {
            lock (lockObject)
            {
                return pendingStateReadWasAuthoritative &&
                       pendingChanges.Changes.All(
                           change => ParseActionType(change.Action) !=
                                     PendingChangeActionType.ApplyStates);
            }
        }

        private void ReplaceWithApplyMarkerNoLock(DateTime timestamp)
        {
            pendingChanges.Changes.Clear();
            pendingChanges.Changes.Add(new AddonChange(ApplyStatesAction, ApplyStatesTarget)
            {
                Timestamp = timestamp
            });
            markerGeneration = Guid.NewGuid();
        }

        private bool TryReadPendingChanges(string path, out PendingChanges loaded)
        {
            loaded = new PendingChanges();
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return true;
                }

                loaded = JsonConvert.DeserializeObject<PendingChanges>(json) ?? new PendingChanges();
                loaded.Changes ??= new List<AddonChange>();
                return true;
            }
            catch (Exception ex)
            {
                errorHandler?.HandleWarning(
                    $"Pending changes file could not be read ({Path.GetFileName(path)}): {ex.Message}",
                    "PendingChangeManager.LoadPendingChanges");
                return false;
            }
        }

        private bool TrySavePendingChangesNoLock()
        {
            var json = JsonConvert.SerializeObject(pendingChanges, Formatting.Indented);
            try
            {
                WriteAtomic(pendingBackupPath, json);
                WriteAtomic(pendingPath, json);
                pendingStateReadWasAuthoritative = true;
                return true;
            }
            catch (Exception ex)
            {
                pendingStateReadWasAuthoritative = false;
                errorHandler?.HandleError(
                    ex,
                    "PendingChangeManager.SavePendingChanges",
                    ErrorSeverity.Warning);
                return false;
            }
        }

        private static void WriteAtomic(string path, string content)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, content);
            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, null);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }

        private void TryDeletePendingFile(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                errorHandler?.HandleWarning(
                    $"Pending changes file could not be deleted ({Path.GetFileName(path)}): {ex.Message}",
                    "PendingChangeManager.ClearPendingChanges");
            }
        }
    }

    public sealed class ChangeAppliedEventArgs : EventArgs
    {
        public AddonChange Change { get; set; } = null!;
    }

    public sealed class ChangeFailedEventArgs : EventArgs
    {
        public AddonChange Change { get; set; } = null!;
        public Exception Error { get; set; } = null!;
    }
}
