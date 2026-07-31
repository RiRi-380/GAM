using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class UndoManagerTests
{
    [Fact]
    public void RecordAction_OverCapacity_KeepsNewestFiftyInUndoOrder()
    {
        var manager = new UndoManager();

        for (var index = 1; index <= 51; index++)
        {
            manager.RecordAction(new UndoAction(
                UndoActionType.AssetCreated,
                $"action-{index}"));
        }

        var poppedDescriptions = new List<string>();
        while (manager.CanUndo)
        {
            poppedDescriptions.Add(manager.PopLastAction()!.Description);
        }

        Assert.Equal(50, poppedDescriptions.Count);
        Assert.Equal("action-51", poppedDescriptions[0]);
        Assert.Equal("action-2", poppedDescriptions[^1]);
        Assert.DoesNotContain("action-1", poppedDescriptions);
    }
}
