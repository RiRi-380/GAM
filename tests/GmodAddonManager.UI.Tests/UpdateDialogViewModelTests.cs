using System.Reactive;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.ViewModels;
using ReactiveUI;

namespace GmodAddonManager.UI.Tests;

public sealed class UpdateDialogViewModelTests
{
    [Fact]
    public async Task RemindLaterCommand_PersistsDeferralBeforeClosing()
    {
        var events = new List<string>();
        var service = new UpdateService("2.0.0");
        var viewModel = new UpdateDialogViewModel(
            service,
            new UpdateInfo { Version = "v2.1.0" },
            async () =>
            {
                await Task.Yield();
                events.Add("deferred");
            });
        viewModel.CloseRequested += (_, result) =>
        {
            Assert.False(result);
            events.Add("closed");
        };

        try
        {
            await ExecuteAsync(viewModel.RemindLaterCommand);

            Assert.Equal(new[] { "deferred", "closed" }, events);
            Assert.False(viewModel.DialogResult);
        }
        finally
        {
            viewModel.Release();
        }
    }

    private static async Task ExecuteAsync(ReactiveCommand<Unit, Unit> command)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var execution = command.Execute().Subscribe(
            _ => { },
            completion.SetException,
            () => completion.SetResult(true));
        await completion.Task;
    }
}
