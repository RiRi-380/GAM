using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GmodAddonManager.UI.Services;
using GmodAddonManager.UI.ViewModels;

namespace GmodAddonManager.UI.Views;

public partial class MainWindow : Window
{
    private TextBox? _searchTextBox;
    
    public MainWindow()
    {
        InitializeComponent();
    }
    
    protected override async void OnOpened(EventArgs e)
    {
        try
        {
            base.OnOpened(e);
            
            // ViewModelのInitializeAsyncを呼び出す
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.StartStartupUpdateCheck();
                await viewModel.InitializeAsync();
            }
            
            // 検索ボックスの参照を取得
            _searchTextBox = this.FindControl<TextBox>("SearchTextBox");
            
            // 検索ボックスのLostFocusイベントをハンドリング
            if (_searchTextBox != null)
            {
                _searchTextBox.LostFocus += (s, args) =>
                {
                    // フォーカスが失われた時の処理
                    // 特に何もしなくても、自動的に青枠とカーソルが消える
                };
            }
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("MainWindow.OnOpened", ex);
        }
    }
    
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        
        // Windowのどこかをクリックした時の処理
        this.AddHandler(PointerPressedEvent, (sender, args) =>
        {
            // クリックされた要素を取得
            var source = args.Source as Control;
            
            // 検索ボックスがフォーカスを持っていて、クリックされた要素が検索ボックスでない場合
            if (_searchTextBox != null && _searchTextBox.IsFocused && source != _searchTextBox)
            {
                // DockPanelにフォーカスを移す（これで検索ボックスのフォーカスが外れる）
                var dockPanel = this.FindControl<DockPanel>("MainDockPanel");
                if (dockPanel != null)
                {
                    dockPanel.Focus();
                }
            }
        }, RoutingStrategies.Tunnel);
    }
}
