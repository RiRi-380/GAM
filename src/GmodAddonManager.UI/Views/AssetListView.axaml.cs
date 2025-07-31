using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GmodAddonManager.UI.ViewModels;
using GmodAddonManager.UI.Services;
using GmodAddonManager.Core.Services;
using GmodAddonManager.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GmodAddonManager.UI.Views;

public partial class AssetListView : UserControl
{
    public AssetListView()
    {
        InitializeComponent();
        
        // ドラッグ&ドロップイベントの登録
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
    }

    private void OnAssetPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && 
            border.DataContext is AssetItemViewModel assetVm &&
            DataContext is AssetListViewModel listVm)
        {
            listVm.SelectedAsset = assetVm;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains("AddonIds") || 
            e.Data.Contains(DataFormats.Text) ||
            e.Data.Contains(DataFormats.Text))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (sender is Border border && 
            (e.Data.Contains("AddonIds") || 
             e.Data.Contains(DataFormats.Text) ||
             e.Data.Contains(DataFormats.Text)))
        {
            border.Classes.Set("dragover", true);
        }
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (sender is Border border)
        {
            border.Classes.Set("dragover", false);
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (sender is Border border)
        {
            border.Classes.Set("dragover", false);
            
            if (border.DataContext is AssetItemViewModel assetVm &&
                DataContext is AssetListViewModel listVm)
            {
                try
                {
                    var dialogService = new DialogService();
                    var addonManager = ViewModelLocator.AddonManager;
                    
                    // Handle addon IDs drop
                    if (e.Data.Contains("AddonIds"))
                    {
                        var addonIds = e.Data.Get("AddonIds") as List<string>;
                        if (addonIds != null && addonIds.Count > 0)
                        {
                            var count = addonIds.Count;
                            var message = count == 1 
                                ? $"1個のアドオンを「{assetVm.Name}」に追加しますか？"
                                : $"{count}個のアドオンを「{assetVm.Name}」に追加しますか？";
                            
                            var confirmed = await dialogService.ShowConfirmAsync("確認", message);
                            if (confirmed)
                            {
                                // アドオンをアセットに追加
                                foreach (var addonId in addonIds)
                                {
                                    assetVm.AddAddon(addonId);
                                }
                                
                                // 設定を保存
                                if (addonManager != null)
                                {
                                    await addonManager.SaveConfigurationAsync();
                                }
                                
                                // UIを更新
                                assetVm.RefreshFromModel(addonManager.GetConfiguration().Assets.First(a => a.Id == assetVm.Id));
                                
                                await dialogService.ShowInfoAsync("完了", 
                                    count == 1 
                                        ? "アドオンを追加しました。" 
                                        : $"{count}個のアドオンを追加しました。");
                                
                                // リロード処理
                                await ReloadAddons();
                            }
                        }
                    }
                    // Handle URL drop
                    else if (e.Data.Contains(DataFormats.Text))
                    {
                        var text = e.Data.GetText();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            var workshopIds = ExtractWorkshopIds(text);
                            if (workshopIds.Count > 0)
                            {
                                var count = workshopIds.Count;
                                var message = count == 1 
                                    ? $"1個のSteamワークショップアドオンを「{assetVm.Name}」に追加しますか？"
                                    : $"{count}個のSteamワークショップアドオンを「{assetVm.Name}」に追加しますか？";
                                
                                var confirmed = await dialogService.ShowConfirmAsync("確認", message);
                                if (confirmed)
                                {
                                    var addedCount = 0;
                                    var errorMessages = new List<string>();
                                    
                                    foreach (var workshopId in workshopIds)
                                    {
                                        try
                                        {
                                            // Check if addon exists locally
                                            var allAddons = addonManager?.GetAllAddons();
                                            WorkshopAddon? addon = null;
                                            if (allAddons != null)
                                            {
                                                foreach (var kvp in allAddons)
                                                {
                                                    if (kvp.Value.Id == workshopId)
                                                    {
                                                        addon = kvp.Value;
                                                        break;
                                                    }
                                                }
                                            }
                                            if (addon != null)
                                            {
                                                assetVm.AddAddon(workshopId);
                                                addedCount++;
                                            }
                                            else
                                            {
                                                // If asset is enabled, we might download it in the future
                                                if (assetVm.IsEnabled)
                                                {
                                                    errorMessages.Add($"アドオン {workshopId} はローカルに存在しません。");
                                                }
                                                else
                                                {
                                                    errorMessages.Add($"アドオン {workshopId} はローカルに存在しません。アセットを有効化してから再度試してください。");
                                                }
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            errorMessages.Add($"アドオン {workshopId} の追加に失敗しました。");
                                        }
                                    }
                                    
                                    // 設定を保存
                                    if (addedCount > 0 && addonManager != null)
                                    {
                                        await addonManager.SaveConfigurationAsync();
                                        assetVm.RefreshFromModel(addonManager.GetConfiguration().Assets.First(a => a.Id == assetVm.Id));
                                    }
                                    
                                    // Show results
                                    if (addedCount > 0)
                                    {
                                        var successMessage = addedCount == 1 
                                            ? "1個のアドオンを追加しました。" 
                                            : $"{addedCount}個のアドオンを追加しました。";
                                        
                                        if (errorMessages.Count > 0)
                                        {
                                            successMessage += "\n\n以下のエラーが発生しました:\n" + string.Join("\n", errorMessages);
                                            await dialogService.ShowWarningAsync("部分的な成功", successMessage);
                                        }
                                        else
                                        {
                                            await dialogService.ShowInfoAsync("完了", successMessage);
                                        }
                                        
                                        // リロード処理
                                        await ReloadAddons();
                                    }
                                    else if (errorMessages.Count > 0)
                                    {
                                        await dialogService.ShowErrorAsync("エラー", string.Join("\n", errorMessages));
                                    }
                                }
                            }
                            else
                            {
                                await dialogService.ShowWarningAsync("無効なURL", "有効なSteamワークショップURLが見つかりませんでした。");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // ViewModelLocator.Logger?.LogError("Failed to handle drop", ex); // Removed logging
                    var dialogService = new DialogService();
                    await dialogService.ShowErrorAsync("エラー", "ドロップ処理に失敗しました。");
                }
            }
        }
    }
    
    private List<string> ExtractWorkshopIds(string text)
    {
        var workshopIds = new List<string>();
        var lines = text.Split(new[] { '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var line in lines)
        {
            var workshopId = SteamUrlParser.ExtractWorkshopId(line.Trim());
            if (!string.IsNullOrEmpty(workshopId) && !workshopIds.Contains(workshopId))
            {
                workshopIds.Add(workshopId);
            }
        }
        
        return workshopIds;
    }
    
    private async Task ReloadAddons()
    {
        try
        {
            // MainWindowViewModelを取得してリロード
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                if (desktop.MainWindow?.DataContext is MainWindowViewModel mainVm)
                {
                    await mainVm.RefreshAddonsAsync();
                }
            }
        }
        catch (Exception ex)
        {
            // ViewModelLocator.Logger?.LogError("Failed to reload addons", ex); // Removed logging
        }
    }
}