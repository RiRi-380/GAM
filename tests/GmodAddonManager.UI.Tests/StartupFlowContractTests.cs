using System.Runtime.CompilerServices;

namespace GmodAddonManager.UI.Tests;

public sealed class StartupFlowContractTests
{
    [Fact]
    public void MainWindowStartupUsesTheAlreadyInitializedManagerAndOneWorkshopLoad()
    {
        var source = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "ViewModels",
            "MainWindowViewModel.cs");
        var initializeMethod = ExtractMethod(
            source,
            "public async Task InitializeAsync(CancellationToken cancellationToken = default)");

        Assert.Contains(
            "await AddonGridViewModel.LoadAddonsAsync(cancellationToken);",
            initializeMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "AssetListViewModel.LoadAssets();",
            initializeMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "SafeFileLogger.TryLogStartupMilestone(\"InventoryReady\");",
            initializeMethod,
            StringComparison.Ordinal);
        Assert.True(
            initializeMethod.IndexOf(
                "await AddonGridViewModel.LoadAddonsAsync(cancellationToken);",
                StringComparison.Ordinal) <
            initializeMethod.IndexOf(
                "AssetListViewModel.LoadAssets();",
                StringComparison.Ordinal),
            "The workshop inventory must update before the asset list is rendered.");

        Assert.DoesNotContain(
            "addonManager.InitializeAsync()",
            initializeMethod,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "addonManager.ScanWorkshopFolderAsync()",
            initializeMethod,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "InitialLoadingWindow",
            initializeMethod,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "NewAddonCheckWindow",
            initializeMethod,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowRendersItsBusyOverlayBeforeStartingInventoryLoad()
    {
        var source = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "MainWindow.axaml.cs");
        var openedMethod = ExtractMethod(
            source,
            "protected override async void OnOpened(EventArgs e)");

        var busyIndex = openedMethod.IndexOf(
            "viewModel.BeginBusy(",
            StringComparison.Ordinal);
        var renderIndex = openedMethod.IndexOf(
            "DispatcherPriority.Render",
            StringComparison.Ordinal);
        var initializeIndex = openedMethod.IndexOf(
            "await viewModel.InitializeAsync(startupToken);",
            StringComparison.Ordinal);

        Assert.True(busyIndex >= 0, "Startup must expose the existing busy overlay.");
        Assert.True(
            renderIndex > busyIndex,
            "The busy state must be set before yielding a render turn.");
        Assert.True(
            initializeIndex > renderIndex,
            "Workshop inventory loading must start only after the render turn.");
        Assert.Contains(
            "startupToken.ThrowIfCancellationRequested();",
            openedMethod,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WorkshopInventoryRunsOnAWorkerAndAppliesBoundStateOnTheUiThread()
    {
        var source = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "ViewModels",
            "AddonGridViewModel.cs");
        var loadMethod = ExtractMethod(
            source,
            "public async Task LoadAddonsAsync(CancellationToken cancellationToken = default)");
        var prepareMethod = ExtractMethod(
            source,
            "private async Task<PreparedAddonInventory> PrepareAddonInventoryAsync(");

        Assert.Contains("await Task.Run(", loadMethod, StringComparison.Ordinal);
        Assert.Contains(
            "PrepareAddonInventoryAsync(sortOptions!, cancellationToken)",
            loadMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "await addonManager.ScanWorkshopFolderAsync().ConfigureAwait(false)",
            prepareMethod,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Dispatcher.UIThread",
            prepareMethod,
            StringComparison.Ordinal);
        Assert.DoesNotContain("AllAddons", prepareMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("AddonItemViewModel", prepareMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("RaisePropertyChanged", prepareMethod, StringComparison.Ordinal);

        var applyIndex = loadMethod.IndexOf(
            "ApplyPreparedAddonInventory(preparedInventory);",
            StringComparison.Ordinal);
        var dispatcherIndex = loadMethod.LastIndexOf(
            "await Dispatcher.UIThread.InvokeAsync(",
            applyIndex,
            StringComparison.Ordinal);
        Assert.True(applyIndex >= 0, "Prepared inventory must be applied.");
        Assert.True(
            dispatcherIndex >= 0 && dispatcherIndex < applyIndex,
            "Observable state must be applied through the UI dispatcher.");
        Assert.Contains(
            "!cancellationToken.IsCancellationRequested",
            loadMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "DispatcherPriority.Normal",
            loadMethod,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ClosingTheMainWindowCancelsPendingStartupInventory()
    {
        var source = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "MainWindow.axaml.cs");
        var closedMethod = ExtractMethod(
            source,
            "protected override void OnClosed(EventArgs e)");
        var disposeMethod = ExtractMethod(
            source,
            "public void Dispose()");

        Assert.Contains("Dispose();", closedMethod, StringComparison.Ordinal);
        Assert.Contains("_startupCancellation.Cancel();", disposeMethod, StringComparison.Ordinal);
        Assert.Contains("_startupCancellation.Dispose();", disposeMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowViewModelDoesNotExposeLegacyStartupDialogs()
    {
        var source = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "ViewModels",
            "MainWindowViewModel.cs");

        Assert.DoesNotContain("ShowInitialLoadingWindow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CheckForNewAddons", source, StringComparison.Ordinal);
        Assert.DoesNotContain("InitialLoadingWindow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NewAddonCheckWindow", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitWorkshopRefreshInvalidatesTheScanCacheBeforeLoading()
    {
        var source = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "ViewModels",
            "MainWindowViewModel.cs");
        var refreshMethod = ExtractMethod(
            source,
            "public async Task RefreshAddonsAsync(bool rescanWorkshop = true, bool showProgress = false)");
        var invalidateIndex = refreshMethod.IndexOf(
            "addonManager.InvalidateWorkshopScanCache();",
            StringComparison.Ordinal);
        var loadIndex = refreshMethod.IndexOf(
            "await AddonGridViewModel.LoadAddonsAsync();",
            invalidateIndex,
            StringComparison.Ordinal);

        Assert.True(invalidateIndex >= 0, "Explicit refresh must invalidate the Workshop scan cache.");
        Assert.True(
            loadIndex > invalidateIndex,
            "The Workshop scan cache must be invalidated before the refreshed load.");
    }

    [Fact]
    public void StartupPathAcceptanceDoesNotApplyDesiredAddonState()
    {
        var source = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Services",
            "StartupPathRecoveryCoordinator.cs");

        Assert.DoesNotContain("UpdateAddonStatesAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("QueueApplyStates", source, StringComparison.Ordinal);
        Assert.Contains("ApplyRepairs = forcePrompt", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleInstanceLockIsAcquiredBeforeStartupPathRecoveryCanSaveSettings()
    {
        var source = ReadRepositoryFile3(
            "src",
            "GmodAddonManager.UI",
            "App.axaml.cs");
        var lockIndex = source.IndexOf("applicationLock.TryAcquireLock()", StringComparison.Ordinal);
        var recoveryIndex = source.IndexOf("StartupPathRecoveryCoordinator.RunStartupAsync", StringComparison.Ordinal);

        Assert.True(lockIndex >= 0, "Startup must acquire the application lock.");
        Assert.True(
            recoveryIndex > lockIndex,
            "Path recovery can save settings and must run only after the single-instance lock is held.");
    }

    [Fact]
    public void StartupPathDiscoveryIsPassedToAddonManagerInsteadOfRepeated()
    {
        var coordinator = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Services",
            "StartupPathRecoveryCoordinator.cs");
        var app = ReadRepositoryFile3(
            "src",
            "GmodAddonManager.UI",
            "App.axaml.cs");
        var manager = ReadRepositoryFile(
            "src",
            "GmodAddonManager.Core",
            "Services",
            "AddonManager.cs");
        var managerConstructor = ExtractMethod(
            manager,
            "public AddonManager(AddonManagerOptions? options)");

        Assert.Contains(
            "ResolvedGmodInstallPath = decision.DetectedGmodInstallPath",
            coordinator,
            StringComparison.Ordinal);
        Assert.Contains(
            "ResolvedWorkshopRootPath = decision.DetectedWorkshopRootPath",
            coordinator,
            StringComparison.Ordinal);
        Assert.Contains(
            "startupPathRecovery.ResolvedGmodInstallPath ??",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "startupPathRecovery.ResolvedWorkshopRootPath ??",
            app,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            coordinator.Split(
                    "new SteamPathDetector().DetectPathSnapshot()",
                    StringSplitOptions.None)
                .Length - 1);
        var overrideIndex = managerConstructor.IndexOf(
            "PathOverrideResolver.TryCreateSnapshot(",
            StringComparison.Ordinal);
        var fallbackIndex = managerConstructor.IndexOf(
            "if (pathSnapshot == null)",
            StringComparison.Ordinal);
        Assert.True(
            overrideIndex >= 0 && fallbackIndex > overrideIndex,
            "AddonManager must validate the passed pair before falling back to Steam discovery.");
    }

    [Fact]
    public void RuntimeWriteGateCombinesWatcherStateWithDirectProcessDetection()
    {
        var source = ReadRepositoryFile3(
            "src",
            "GmodAddonManager.UI",
            "App.axaml.cs");

        Assert.Contains("initializedProcessWatcher.IsGmodRunning", source, StringComparison.Ordinal);
        Assert.Contains("SteamProcessChecker.IsGmodRunning()", source, StringComparison.Ordinal);

        var providerIndex = source.IndexOf(
            "initializedAddonManager.GmodRunningProvider = () =>",
            StringComparison.Ordinal);
        var startupPendingIndex = source.IndexOf(
            "await pendingChangeManager.ApplyPendingChangesAsync();",
            StringComparison.Ordinal);
        Assert.True(providerIndex >= 0, "Startup must install the GMod-running authority.");
        Assert.True(
            startupPendingIndex > providerIndex,
            "The GMod-running authority must be installed before pending startup apply.");
    }

    [Fact]
    public void StartupPathRecoveryDialogLocalizesReasonsAndKeepsFooterSeparate()
    {
        var codeBehind = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "StartupPathRecoveryDialog.axaml.cs");
        var markup = ReadRepositoryFile(
            "src",
            "GmodAddonManager.UI",
            "Views",
            "StartupPathRecoveryDialog.axaml");

        Assert.Contains(
            "ReasonText.Text = L.Get($\"StartupPathRecovery.Reason.{decision.Reason}\")",
            codeBehind,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ReasonText.Text = decision.Reason", codeBehind, StringComparison.Ordinal);
        Assert.Contains(
            "StartupPathRecoveryReason.WorkshopPathUnavailable => \"StartupPathRecovery.Heading.WorkshopUnavailable\"",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "StatusText.Text = L.Get(\"StartupPathRecovery.InvalidFolder\")",
            codeBehind,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "L.Format(\"StartupPathRecovery.InvalidFolder\"",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains("x:Name=\"HeadingText\"", markup, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DescriptionText\"", markup, StringComparison.Ordinal);
        Assert.Contains("<Grid Grid.Row=\"2\" RowDefinitions=\"Auto,Auto\">", markup, StringComparison.Ordinal);
        Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", markup, StringComparison.Ordinal);
    }

    private static string ExtractMethod(string source, string signature)
    {
        var methodIndex = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(methodIndex >= 0, $"Method not found: {signature}");

        var braceIndex = source.IndexOf('{', methodIndex);
        Assert.True(braceIndex >= 0, $"Method body not found: {signature}");

        var depth = 0;
        for (var i = braceIndex; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(methodIndex, i - methodIndex + 1);
                }
            }
        }

        throw new InvalidOperationException($"Method body did not close: {signature}");
    }

    private static string ReadRepositoryFile3(
        string segment,
        string segment2,
        string segment3,
        [CallerFilePath] string sourceFilePath = "")
    {
        return ReadRepositoryFile(
            new[] { segment, segment2, segment3 },
            sourceFilePath);
    }

    private static string ReadRepositoryFile(
        string segment,
        string segment2,
        string segment3,
        string segment4,
        [CallerFilePath] string sourceFilePath = "")
    {
        return ReadRepositoryFile(
            new[] { segment, segment2, segment3, segment4 },
            sourceFilePath);
    }

    private static string ReadRepositoryFile(
        string[] segments,
        string sourceFilePath)
    {
        var directory = new FileInfo(sourceFilePath).Directory;

        while (directory is not null)
        {
            var candidate =
                Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not find repository file: {Path.Combine(segments)}",
            Path.Combine(segments));
    }
}
