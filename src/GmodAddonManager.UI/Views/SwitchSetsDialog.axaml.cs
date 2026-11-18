using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using GmodAddonManager.UI.Services;

namespace GmodAddonManager.UI.Views;

public sealed class SwitchSetRow
{
    public SwitchSet Set { get; }
    public string Name => Set.Name;
    public string Changes { get; }

    internal SwitchSetRow(SwitchSet set, AddonManager manager)
    {
        Set = set;
        var diff = manager.GetSwitchSetChanges(set);
        Changes = L.Format("SwitchSets.Changes", diff.Enabled, diff.Disabled, diff.Excluded);
        if (diff.NewAssets > 0 || diff.RemovedAssets > 0)
        {
            Changes += " " + L.Format("SwitchSets.Missing", diff.NewAssets, diff.RemovedAssets);
        }
    }
}

public partial class SwitchSetsDialog : Window
{
    private readonly AddonManager? manager;
    private SwitchSet? renaming;
    private SwitchSet? deleting;
    private bool busy;
    public bool ApplyAttempted { get; private set; }

    public SwitchSetsDialog()
    {
        InitializeComponent();
        Closing += (_, args) => args.Cancel = busy;
    }

    public SwitchSetsDialog(AddonManager manager) : this()
    {
        this.manager = manager;
        Reload();
    }

    private void Reload()
    {
        Rows.ItemsSource = manager!.GetConfiguration().SwitchSets.Select(set => new SwitchSetRow(set, manager)).ToList();
        ValidateName();
    }

    private void ValidateName()
    {
        if (SaveButton == null || manager == null) return;
        var name = SetName.Text?.Trim();
        var duplicate = manager.GetConfiguration().SwitchSets.Any(set => set.Id != renaming?.Id &&
            string.Equals(set.Name, name, StringComparison.OrdinalIgnoreCase));
        SaveButton.IsEnabled = !string.IsNullOrEmpty(name) && !duplicate && name != renaming?.Name;
        Feedback.Text = duplicate ? L.Get("SwitchSets.Duplicate") : string.Empty;
    }

    private void OnNameChanged(object? sender, TextChangedEventArgs args) => ValidateName();

    private void SetRenameTarget(SwitchSet? set)
    {
        renaming = set;
        SaveButton.Content = L.Get(set == null ? "SwitchSets.Save" : "SwitchSets.SaveName");
        CancelRename.IsVisible = set != null;
        SetName.Text = set?.Name ?? string.Empty;
        ValidateName();
    }

    private void OnRename(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: SwitchSetRow row })
        {
            SetRenameTarget(row.Set);
            SetName.Focus();
            SetName.SelectAll();
        }
    }

    private void OnCancelRename(object? sender, RoutedEventArgs args) => SetRenameTarget(null);

    private async void OnSave(object? sender, RoutedEventArgs args)
    {
        if (manager == null || !SaveButton.IsEnabled) return;
        var name = SetName.Text!.Trim();
        await RunAsync(async () =>
        {
            if (renaming == null) await manager.SaveSwitchSetAsync(name);
            else await manager.RenameSwitchSetAsync(renaming.Id, name);
            SetRenameTarget(null);
            Reload();
        });
    }

    private async void OnApply(object? sender, RoutedEventArgs args)
    {
        if (manager == null || sender is not Button { DataContext: SwitchSetRow row }) return;
        ApplyAttempted = true;
        if (await RunAsync(() => manager.ApplySwitchSetAsync(row.Set.Id))) Close();
    }

    private void SetDeleteTarget(SwitchSet? set)
    {
        deleting = set;
        Rows.IsEnabled = set == null;
        Editor.IsVisible = set == null;
        DeletePrompt.IsVisible = set != null;
        DeleteMessage.Text = set == null ? string.Empty : L.Format("SwitchSets.ConfirmDelete", set.Name);
    }

    private void OnRequestDelete(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: SwitchSetRow row }) SetDeleteTarget(row.Set);
    }

    private void OnCancelDelete(object? sender, RoutedEventArgs args) => SetDeleteTarget(null);

    private async void OnDelete(object? sender, RoutedEventArgs args)
    {
        var target = deleting;
        if (manager == null || target == null) return;
        await RunAsync(async () =>
        {
            await manager.DeleteSwitchSetAsync(target.Id);
            SetDeleteTarget(null);
            if (renaming?.Id == target.Id) SetRenameTarget(null);
            Reload();
        });
    }

    private async Task<bool> RunAsync(Func<Task> operation)
    {
        if (busy) return false;
        busy = true;
        IsEnabled = false;
        try
        {
            await operation();
            return true;
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("SwitchSetsDialog", ex);
            Feedback.Text = L.Get("SwitchSets.Failed");
            return false;
        }
        finally
        {
            IsEnabled = true;
            busy = false;
        }
    }

    private void OnClose(object? sender, RoutedEventArgs args) => Close();
}
