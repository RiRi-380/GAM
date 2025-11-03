using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GmodAddonManager.UI.ViewModels;
using GmodAddonManager.UI.Services;
using GmodAddonManager.Core.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace GmodAddonManager.UI.Views;

public partial class AddonGridView : UserControl
{
    private Point? _dragStartPoint;
    private bool _isDragging;
    private double _lastScrollOffset = 0;

    public AddonGridView()
    {
        InitializeComponent();
        
    }
    
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        
        // ScrollViewerの設定
        Dispatcher.UIThread.Post(() =>
        {
            // ScrollViewerのPropertyChangedイベントを監視
            var scrollViewer = this.FindControl<ScrollViewer>("AddonScrollViewer");
            if (scrollViewer != null)
            {
                scrollViewer.GetObservable(ScrollViewer.OffsetProperty)
                    .Throttle(TimeSpan.FromMilliseconds(50))
                    .Subscribe(_ => OnScrollChanged());
                    
                // ItemsRepeaterのEffectiveViewportChangedイベントも監視
                var itemsRepeater = this.FindControl<ItemsRepeater>("AddonItemsRepeater");
                if (itemsRepeater != null)
                {
                    itemsRepeater.EffectiveViewportChanged += OnEffectiveViewportChanged;
                }
            }
            
            // ComboBoxのイベントハンドラーを設定
            var stateChangeComboBox = this.FindControl<ComboBox>("StateChangeComboBox");
            if (stateChangeComboBox != null)
            {
                stateChangeComboBox.SelectionChanged += OnStateChangeComboBoxSelectionChanged;
            }
        }, DispatcherPriority.Background);
    }
    
    private async void OnStateChangeComboBoxSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem item)
        {
            var action = item.Content?.ToString();
            if (!string.IsNullOrWhiteSpace(action) && DataContext is AddonGridViewModel vm)
            {
                await vm.ChangeSelectedAddonStateCommand.Execute(action);
                
                // ComboBoxを初期状態に戻す
                comboBox.SelectedIndex = -1;
            }
        }
    }


    private void OnAddonPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && 
            border.DataContext is AddonItemViewModel addonVm &&
            DataContext is AddonGridViewModel gridVm)
        {
            var point = e.GetCurrentPoint(this);
            var isCtrlPressed = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            
            if (point.Properties.IsLeftButtonPressed)
            {
                // 選択処理（選択モードでなくても選択を許可）
                gridVm.SelectAddon(addonVm.AddonId, isCtrlPressed);
                
                // 選択モードでない場合は自動的に選択モードに切り替え
                if (!gridVm.IsSelectionMode && gridVm.HasSelectedAddons)
                {
                    gridVm.IsSelectionMode = true;
                }
                
                // ドラッグ開始位置を記録
                _dragStartPoint = point.Position;
                _isDragging = false;
                
                // ポインター移動イベントを登録
                border.PointerMoved += OnAddonPointerMoved;
                border.PointerReleased += OnAddonPointerReleased;
            }
            else if (point.Properties.IsRightButtonPressed)
            {
                // 右クリックで詳細表示
                gridVm.SelectedAddon = addonVm;
                
                // 詳細情報をロード（まだロードされていない場合）
                if (!addonVm.IsDetailsLoaded)
                {
                    _ = gridVm.LoadDetailsCommand.Execute(addonVm).FirstAsync();
                }
            }
        }
    }

    private async void OnAddonPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is Border border && 
            border.DataContext is AddonItemViewModel addonVm &&
            DataContext is AddonGridViewModel gridVm &&
            _dragStartPoint.HasValue &&
            !_isDragging)
        {
            // 選択モードでない場合はドラッグを許可しない
            if (!gridVm.IsSelectionMode)
            {
                return;
            }
            
            var point = e.GetCurrentPoint(this);
            var distance = Math.Abs(point.Position.X - _dragStartPoint.Value.X) +
                          Math.Abs(point.Position.Y - _dragStartPoint.Value.Y);
            
            // ドラッグ開始のしきい値
            if (distance > 5)
            {
                _isDragging = true;
                
                // ドラッグデータの準備
                var selectedAddons = gridVm.GetSelectedAddons();
                if (!selectedAddons.Any(a => a.AddonId == addonVm.AddonId))
                {
                    // 現在のアイテムが選択されていない場合は、これだけを選択
                    gridVm.ClearSelection();
                    gridVm.SelectAddon(addonVm.AddonId);
                    selectedAddons = gridVm.GetSelectedAddons();
                }
                
                var dragData = new DataObject();
                var addonIds = selectedAddons.Select(a => a.AddonId).ToList();
                dragData.Set("AddonIds", addonIds);
                dragData.Set("DraggedAddons", selectedAddons);
                
                // ドラッグ開始
                var result = await DragDrop.DoDragDrop(e, dragData, DragDropEffects.Copy | DragDropEffects.Move);
                
                // クリーンアップ
                _isDragging = false;
                _dragStartPoint = null;
            }
        }
    }

    private void OnAddonPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Border border)
        {
            // イベントハンドラーの削除
            border.PointerMoved -= OnAddonPointerMoved;
            border.PointerReleased -= OnAddonPointerReleased;
            
            _dragStartPoint = null;
            _isDragging = false;
        }
    }

    private async void OnAddonDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border border && 
            border.DataContext is AddonItemViewModel addonVm &&
            DataContext is AddonGridViewModel gridVm)
        {
            // 詳細情報をロード
            if (!addonVm.IsDetailsLoaded)
            {
                await gridVm.LoadDetailsCommand.Execute(addonVm).FirstAsync();
            }
            
            // フォルダーを開く
            addonVm.OpenFolderCommand.Execute().Subscribe();
        }
    }


    private void OnScrollChanged()
    {
        try
        {
            if (DataContext is AddonGridViewModel gridVm)
            {
                var scrollViewer = this.FindControl<ScrollViewer>("AddonScrollViewer");
                if (scrollViewer != null)
                {
                    var currentOffset = scrollViewer.Offset.Y;
                    if (Math.Abs(currentOffset - _lastScrollOffset) > 10) // 10ピクセル以上スクロールした場合
                    {
                        _lastScrollOffset = currentOffset;
                        
                        
                        _ = gridVm.LoadVisibleAddonDetailsAsync();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // ViewModelLocator.Logger?.LogError("Error in OnScrollChanged", ex); // Removed logging
        }
    }
    
    private void OnEffectiveViewportChanged(object? sender, EventArgs e)
    {
        try
        {
            if (DataContext is AddonGridViewModel gridVm && sender is ItemsRepeater itemsRepeater)
            {
                // ItemsRepeaterの表示領域を取得
                var scrollViewer = this.FindControl<ScrollViewer>("AddonScrollViewer");
                if (scrollViewer == null) return;
                
                var viewport = new Rect(0, scrollViewer.Offset.Y, scrollViewer.Viewport.Width, scrollViewer.Viewport.Height);
                
                // ビューポート内のアイテムインデックスを計算
                var itemHeight = 240; // アイテムの高さ（概算）
                var columns = 5; // 列数（概算）
                
                var startIndex = Math.Max(0, (int)(viewport.Y / itemHeight) * columns);
                var visibleRows = Math.Ceiling(viewport.Height / itemHeight) + 1;
                var endIndex = Math.Min(gridVm.FilteredAddons.Count, startIndex + (int)(visibleRows * columns));
                
                // 実際に表示されているアイテムのみを読み込む
                _ = gridVm.LoadVisibleRangeAsync(startIndex, endIndex);
                
            }
        }
        catch (Exception ex)
        {
            // ViewModelLocator.Logger?.LogError("Error in OnEffectiveViewportChanged", ex); // Removed logging
        }
    }
}