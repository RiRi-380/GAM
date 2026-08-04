using Avalonia.Controls;
using Avalonia.Interactivity;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.ViewModels;
using System;

namespace GmodAddonManager.UI.Views;

public partial class PathHealthDialog : Window
{
    private readonly DialogService dialogService = new DialogService();

    public PathHealthDialog()
    {
        InitializeComponent();
    }

    public PathHealthDialog(PathHealthViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void OnRefresh(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PathHealthViewModel viewModel)
        {
            viewModel.Refresh();
        }
    }

    private async void OnRepairMetadata(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not PathHealthViewModel viewModel)
            {
                return;
            }

            var confirmed = await dialogService.ShowConfirmAsync(
                L.Get("PathHealth.ConfirmTitle"),
                L.Format("PathHealth.ConfirmMetadataRepair", viewModel.MetadataRepairCount));
            if (confirmed)
            {
                await viewModel.RepairMetadataAsync();
            }
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("PathHealthDialog.OnRepairMetadata", ex);
        }
    }

    private async void OnMigrateAddonNoMount(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not PathHealthViewModel viewModel)
            {
                return;
            }

            var confirmed = await dialogService.ShowConfirmAsync(
                L.Get("PathHealth.ConfirmTitle"),
                L.Format("PathHealth.ConfirmAddonNoMountMigration", viewModel.AddonNoMountMigrationCount));
            if (confirmed)
            {
                await viewModel.MigrateAddonNoMountAsync();
            }
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("PathHealthDialog.OnMigrateAddonNoMount", ex);
        }
    }

    private async void OnMigrateManagedData(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not PathHealthViewModel viewModel)
            {
                return;
            }

            var confirmed = await dialogService.ShowConfirmAsync(
                L.Get("PathHealth.ConfirmTitle"),
                L.Format("PathHealth.ConfirmManagedMigration", viewModel.ManagedMigrationCandidateCount));
            if (confirmed)
            {
                await viewModel.MigrateManagedDataAsync();
            }
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("PathHealthDialog.OnMigrateManagedData", ex);
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
