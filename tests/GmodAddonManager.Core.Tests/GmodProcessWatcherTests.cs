using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class GmodProcessWatcherTests
{
    [Theory]
    [InlineData("gmod", true)]
    [InlineData("gmod.exe", true)]
    [InlineData("GMOD.EXE", true)]
    [InlineData("hl2", true)]
    [InlineData("hl2.exe", true)]
    [InlineData("gmod-helper", false)]
    [InlineData("GmodAddonManager.UI", false)]
    [InlineData("GmodAddonManager.UI.exe", false)]
    [InlineData("my-hl2-tool.exe", false)]
    [InlineData("", false)]
    public void IsRecognizedProcessName_CoversCurrentAndLegacyExecutables(
        string processName,
        bool expected)
    {
        Assert.Equal(expected, GmodProcessWatcher.IsRecognizedProcessName(processName));
    }

    [Fact]
    public void RefreshProcessState_RaisesStartedForCurrentGmodExecutableSnapshot()
    {
        IReadOnlyCollection<int> snapshot = new[] { 2002 };
        using var watcher = new GmodProcessWatcher(() => snapshot);
        var startedEvents = new List<ProcessEventArgs>();
        watcher.GmodStarted += (_, e) => startedEvents.Add(e);

        watcher.RefreshProcessState();

        Assert.True(watcher.IsGmodRunning);
        var started = Assert.Single(startedEvents);
        Assert.Equal(2002, started.ProcessId);
    }

    [Fact]
    public void ObserveProcessStopped_WaitsForLastRecognizedProcess()
    {
        IReadOnlyCollection<int> snapshot = new[] { 1001, 2002 };
        using var watcher = new GmodProcessWatcher(() => snapshot);
        var stoppedEvents = new List<ProcessEventArgs>();
        watcher.GmodStopped += (_, e) => stoppedEvents.Add(e);
        watcher.RefreshProcessState();

        watcher.ObserveProcessStopped(1001);

        Assert.True(watcher.IsGmodRunning);
        Assert.Empty(stoppedEvents);

        watcher.ObserveProcessStopped(2002);

        Assert.False(watcher.IsGmodRunning);
        var stopped = Assert.Single(stoppedEvents);
        Assert.Equal(2002, stopped.ProcessId);
    }

    [Fact]
    public void ObserveProcessStarted_DuplicateEventDoesNotRaiseDuplicateTransition()
    {
        using var watcher = new GmodProcessWatcher(() => Array.Empty<int>());
        var startedCount = 0;
        watcher.GmodStarted += (_, _) => startedCount++;

        watcher.ObserveProcessStarted(3003);
        watcher.ObserveProcessStarted(3003);

        Assert.True(watcher.IsGmodRunning);
        Assert.Equal(1, startedCount);
    }

    [Fact]
    public void RefreshProcessState_EnumerationFailurePreservesKnownRunningState()
    {
        var failRefresh = false;
        using var watcher = new GmodProcessWatcher(() =>
        {
            if (failRefresh)
            {
                throw new InvalidOperationException("simulated process enumeration failure");
            }

            return new[] { 4004 };
        });
        var stoppedCount = 0;
        watcher.GmodStopped += (_, _) => stoppedCount++;
        watcher.RefreshProcessState();
        failRefresh = true;

        watcher.RefreshProcessState();

        Assert.True(watcher.IsGmodRunning);
        Assert.Equal(0, stoppedCount);
    }
}
