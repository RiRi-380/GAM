using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class GmodDisabledAddonReconciliationServiceTests
{
    private const string StateStorePath = @"C:\Games\GarrysMod\garrysmod\cfg\addonnomount.txt";
    private readonly GmodDisabledAddonReconciliationService service = new();

    [Fact]
    public void CreateDefaultAssets_PutsFixedDisabledAssetDirectlyAfterSubscribe()
    {
        var configuration = new Configuration();

        configuration.CreateDefaultAssets(includeJunction: true);

        Assert.Equal(
            [
                SystemAssetDefinitions.SubscribeId,
                SystemAssetDefinitions.GmodDisabledId,
                SystemAssetDefinitions.JunctionId
            ],
            configuration.Assets.Select(asset => asset.Id));
        var disabled = configuration.Assets[1];
        Assert.Equal(SystemAssetDefinitions.GmodDisabledName, disabled.Name);
        Assert.True(disabled.IsSystem);
        Assert.Equal(AddonState.Disabled, SystemAssetDefinitions.GmodDisabledDefaultState);
        Assert.Equal(SystemAssetDefinitions.GmodDisabledDefaultState, disabled.GetWholeState());
        Assert.Empty(disabled.Addons);
    }

    [Fact]
    public void EnsureSystemAsset_RecreatesMissingFixedAssetWithDefaultOff()
    {
        var configuration = CreateConfiguration();
        configuration.Assets.Remove(DisabledAsset(configuration));

        var result = service.EnsureSystemAsset(
            configuration,
            absorbUntouchedLegacyImport: false);

        Assert.True(result.Changed);
        Assert.Equal(SystemAssetDefinitions.GmodDisabledId, result.Asset.Id);
        Assert.Equal(AddonState.Disabled, result.Asset.GetWholeState());
        Assert.Same(result.Asset, DisabledAsset(configuration));
    }

    [Theory]
    [InlineData(AddonState.Enabled)]
    [InlineData(AddonState.Disabled)]
    [InlineData(AddonState.Excluded)]
    public void Reconcile_PreservesEveryValidWholeAssetState(AddonState state)
    {
        var configuration = CreateConfiguration();
        DisabledAsset(configuration).SetWholeState(state);

        Reconcile(
            configuration,
            subscribed: ["100"],
            disabled: [],
            allowInitialSeed: false);

        Assert.Equal(state, DisabledAsset(configuration).GetWholeState());
    }

    [Fact]
    public void EnsureSystemAsset_RepairsUnknownWholeAssetStateToDefaultOff()
    {
        var configuration = CreateConfiguration();
        DisabledAsset(configuration).State = (AddonState)999;

        var result = service.EnsureSystemAsset(
            configuration,
            absorbUntouchedLegacyImport: false);

        Assert.True(result.Changed);
        Assert.Equal(SystemAssetDefinitions.GmodDisabledDefaultState, result.Asset.GetWholeState());
    }

    [Fact]
    public void Reconcile_BrandNewProfileSeedsOnlySubscribedDisabledIds()
    {
        var configuration = CreateConfiguration(initialImportCompleted: false);

        var result = Reconcile(
            configuration,
            subscribed: ["100", "200"],
            disabled: ["200", "300"],
            allowInitialSeed: true);

        Assert.True(result.MembershipChanged);
        Assert.Equal(["200"], DisabledAsset(configuration).Addons);
        Assert.Equal(
            new Dictionary<string, bool>
            {
                ["100"] = true,
                ["200"] = false
            },
            configuration.LastObservedGmodAddonStates);
        Assert.Equal(StateStorePath, configuration.LastObservedGmodStateStorePath);
        Assert.True(configuration.InitialRuntimeImportCompleted);
    }

    [Fact]
    public void Reconcile_ExternalDisableAddsAndExternalEnableRemovesMember()
    {
        var configuration = CreateConfiguration();
        Reconcile(configuration, ["100"], [], allowInitialSeed: false);

        var disabled = Reconcile(
            configuration,
            ["100"],
            ["100"],
            allowInitialSeed: false);

        Assert.True(disabled.MembershipChanged);
        Assert.Equal(["100"], DisabledAsset(configuration).Addons);

        var enabled = Reconcile(
            configuration,
            ["100"],
            [],
            allowInitialSeed: false);

        Assert.True(enabled.MembershipChanged);
        Assert.Empty(DisabledAsset(configuration).Addons);
    }

    [Fact]
    public void Reconcile_SuccessfulGamDisableDoesNotBecomeGmodDisabledMember()
    {
        var configuration = CreateConfiguration();
        Reconcile(configuration, ["100"], [], allowInitialSeed: false);
        service.RecordSuccessfulGamWrite(
            configuration,
            new Dictionary<string, bool> { ["100"] = false },
            DateTime.UtcNow,
            StateStorePath);

        Reconcile(
            configuration,
            ["100"],
            ["100"],
            allowInitialSeed: false);

        Assert.Empty(DisabledAsset(configuration).Addons);
        Assert.False(configuration.LastGamAppliedAddonStates["100"]);
    }

    [Fact]
    public void Reconcile_SuccessfulGamEnableDoesNotRemoveExistingGmodOriginMember()
    {
        var configuration = CreateConfiguration(initialImportCompleted: false);
        Reconcile(
            configuration,
            ["100"],
            ["100"],
            allowInitialSeed: true);
        service.RecordSuccessfulGamWrite(
            configuration,
            new Dictionary<string, bool> { ["100"] = true },
            DateTime.UtcNow,
            StateStorePath);

        Reconcile(configuration, ["100"], [], allowInitialSeed: false);

        Assert.Equal(["100"], DisabledAsset(configuration).Addons);
    }

    [Fact]
    public void Reconcile_UnsubscribePrunesAndResubscribeIsFirstObservationOnly()
    {
        var configuration = CreateConfiguration(initialImportCompleted: false);
        Reconcile(
            configuration,
            ["100"],
            ["100"],
            allowInitialSeed: true);

        Reconcile(configuration, [], ["100"], allowInitialSeed: false);
        Assert.Empty(DisabledAsset(configuration).Addons);

        Reconcile(
            configuration,
            ["100"],
            ["100"],
            allowInitialSeed: false);

        Assert.Empty(DisabledAsset(configuration).Addons);
        Assert.False(configuration.LastObservedGmodAddonStates["100"]);
    }

    [Fact]
    public void Reconcile_StateStorePathChangeRebaselinesWithoutMutatingMembership()
    {
        var configuration = CreateConfiguration(initialImportCompleted: false);
        Reconcile(
            configuration,
            ["100"],
            ["100"],
            allowInitialSeed: true);

        service.ReconcileValidObservation(
            configuration,
            ["100"],
            [],
            DateTime.UtcNow,
            allowInitialSeed: false,
            stateStorePath: @"D:\Steam\GarrysMod\garrysmod\cfg\addonnomount.txt");

        Assert.Equal(["100"], DisabledAsset(configuration).Addons);
        Assert.True(configuration.LastObservedGmodAddonStates["100"]);
        Assert.Equal(
            @"D:\Steam\GarrysMod\garrysmod\cfg\addonnomount.txt",
            configuration.LastObservedGmodStateStorePath);
    }

    [Fact]
    public void RecoverPendingWrite_TargetMatchCompletesWithoutImportingGamChange()
    {
        var configuration = CreateConfiguration();
        Reconcile(configuration, ["100"], [], allowInitialSeed: false);
        configuration.PendingGamRuntimeWrite = service.CreatePendingWrite(
            new Dictionary<string, bool> { ["100"] = false },
            new Dictionary<string, bool> { ["100"] = true },
            DateTime.UtcNow,
            StateStorePath);

        var result = Reconcile(
            configuration,
            ["100"],
            ["100"],
            allowInitialSeed: false);

        Assert.Equal(PendingGamRuntimeWriteRecovery.Completed, result.PendingRecovery);
        Assert.Null(configuration.PendingGamRuntimeWrite);
        Assert.Empty(DisabledAsset(configuration).Addons);
        Assert.False(configuration.LastGamAppliedAddonStates["100"]);
    }

    [Fact]
    public void RecoverPendingWrite_PreviousMatchRetainsJournalUntilMarkerIsDurable()
    {
        var configuration = CreateConfiguration();
        Reconcile(configuration, ["100"], [], allowInitialSeed: false);
        var pending = service.CreatePendingWrite(
            new Dictionary<string, bool> { ["100"] = false },
            new Dictionary<string, bool> { ["100"] = true },
            DateTime.UtcNow,
            StateStorePath);
        configuration.PendingGamRuntimeWrite = pending;

        var result = Reconcile(
            configuration,
            ["100"],
            [],
            allowInitialSeed: false);

        Assert.Equal(PendingGamRuntimeWriteRecovery.NotApplied, result.PendingRecovery);
        Assert.Same(pending, configuration.PendingGamRuntimeWrite);
        Assert.False(pending.ConflictDetected);
    }

    [Fact]
    public void RecoverPendingWrite_MixedStateLatchesConflictAndAttributesOnlyInferableGamPart()
    {
        var configuration = CreateConfiguration();
        Reconcile(configuration, ["100", "200"], [], allowInitialSeed: false);
        var pending = service.CreatePendingWrite(
            new Dictionary<string, bool>
            {
                ["100"] = false,
                ["200"] = false
            },
            new Dictionary<string, bool>
            {
                ["100"] = true,
                ["200"] = true
            },
            DateTime.UtcNow,
            StateStorePath);
        configuration.PendingGamRuntimeWrite = pending;

        var result = Reconcile(
            configuration,
            ["100", "200"],
            ["100"],
            allowInitialSeed: false);

        Assert.Equal(PendingGamRuntimeWriteRecovery.Conflicted, result.PendingRecovery);
        Assert.Same(pending, configuration.PendingGamRuntimeWrite);
        Assert.True(pending.ConflictDetected);
        Assert.False(configuration.LastGamAppliedAddonStates["100"]);
        Assert.False(configuration.LastObservedGmodAddonStates["100"]);
        Assert.True(configuration.LastObservedGmodAddonStates["200"]);
        Assert.Empty(DisabledAsset(configuration).Addons);
    }

    [Fact]
    public void RecoverPendingWrite_PrunesUnsubscribedScopeWithoutFalseConflictOrResubscribeImport()
    {
        var configuration = CreateConfiguration();
        Reconcile(configuration, ["100", "200"], [], allowInitialSeed: false);
        configuration.PendingGamRuntimeWrite = service.CreatePendingWrite(
            new Dictionary<string, bool>
            {
                ["100"] = false,
                ["200"] = false
            },
            new Dictionary<string, bool>
            {
                ["100"] = true,
                ["200"] = true
            },
            DateTime.UtcNow,
            StateStorePath);

        var remainingScope = Reconcile(
            configuration,
            ["100"],
            ["100"],
            allowInitialSeed: false);

        Assert.Equal(
            PendingGamRuntimeWriteRecovery.Completed,
            remainingScope.PendingRecovery);
        Assert.Null(configuration.PendingGamRuntimeWrite);
        Assert.Empty(DisabledAsset(configuration).Addons);

        Reconcile(
            configuration,
            ["100", "200"],
            ["100", "200"],
            allowInitialSeed: false);

        Assert.Empty(DisabledAsset(configuration).Addons);
        Assert.False(configuration.LastObservedGmodAddonStates["200"]);
    }

    [Fact]
    public void RecoverPendingWrite_StateStoreMismatchLatchesConflictWithoutChangingMembershipOrAck()
    {
        var configuration = CreateConfiguration();
        Reconcile(configuration, ["100"], [], allowInitialSeed: false);
        DisabledAsset(configuration).Addons = ["100"];
        configuration.PendingGamRuntimeWrite = service.CreatePendingWrite(
            new Dictionary<string, bool> { ["100"] = false },
            new Dictionary<string, bool> { ["100"] = true },
            DateTime.UtcNow,
            @"D:\OtherLibrary\garrysmod\cfg\addonnomount.txt");

        var result = Reconcile(
            configuration,
            ["100"],
            [],
            allowInitialSeed: false);

        Assert.Equal(PendingGamRuntimeWriteRecovery.Conflicted, result.PendingRecovery);
        Assert.Equal(["100"], DisabledAsset(configuration).Addons);
        Assert.True(configuration.LastObservedGmodAddonStates["100"]);
        Assert.True(configuration.PendingGamRuntimeWrite?.ConflictDetected);
    }

    [Fact]
    public void Reconcile_FirstMigrationObservationAddsOnlyUnexpectedOffAndPrunesNowOnLegacyMember()
    {
        var configuration = CreateConfiguration();
        configuration.GmodAttributionMigrationPending = true;
        DisabledAsset(configuration).Addons = ["100"];

        Reconcile(
            configuration,
            ["100", "200", "300"],
            ["200", "300"],
            allowInitialSeed: false,
            migrationDesiredStates: new Dictionary<string, bool>
            {
                ["100"] = true,
                ["200"] = true,
                ["300"] = false
            });

        Assert.Equal(["200"], DisabledAsset(configuration).Addons);
        Assert.False(configuration.GmodAttributionMigrationPending);
    }

    private GmodDisabledAddonReconciliationResult Reconcile(
        Configuration configuration,
        IEnumerable<string> subscribed,
        IEnumerable<string> disabled,
        bool allowInitialSeed,
        IReadOnlyDictionary<string, bool>? migrationDesiredStates = null)
    {
        return service.ReconcileValidObservation(
            configuration,
            subscribed,
            disabled,
            DateTime.UtcNow,
            allowInitialSeed,
            StateStorePath,
            migrationDesiredStates);
    }

    private static Configuration CreateConfiguration(
        bool initialImportCompleted = true)
    {
        var configuration = new Configuration
        {
            InitialRuntimeImportCompleted = initialImportCompleted
        };
        configuration.CreateDefaultAssets();
        return configuration;
    }

    private static Asset DisabledAsset(Configuration configuration)
    {
        return configuration.Assets.Single(
            asset => asset.Id == SystemAssetDefinitions.GmodDisabledId);
    }
}
