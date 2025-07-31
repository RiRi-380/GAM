using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using GmodAddonManager.Core.Models;
using System;

namespace GmodAddonManager.UI.Views;

public partial class AddonStateControl : UserControl
{
    public static readonly StyledProperty<AddonState> StateProperty =
        AvaloniaProperty.Register<AddonStateControl, AddonState>(
            nameof(State),
            AddonState.Enabled,
            defaultBindingMode: BindingMode.TwoWay);
    
    public AddonState State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }
    
    // ViewModel用のプロパティ
    public new bool IsEnabled
    {
        get => State == AddonState.Enabled;
        set { if (value) State = AddonState.Enabled; }
    }
    
    public bool IsDisabled
    {
        get => State == AddonState.Disabled;
        set { if (value) State = AddonState.Disabled; }
    }
    
    public bool IsExcluded
    {
        get => State == AddonState.Excluded;
        set { if (value) State = AddonState.Excluded; }
    }
    
    public AddonStateControl()
    {
        InitializeComponent();
        DataContext = this;
        
        // State変更時にRadioButtonの状態を更新
        this.GetObservable(StateProperty).Subscribe(state =>
        {
            switch (state)
            {
                case AddonState.Enabled:
                    EnabledButton.IsChecked = true;
                    break;
                case AddonState.Disabled:
                    DisabledButton.IsChecked = true;
                    break;
                case AddonState.Excluded:
                    ExcludedButton.IsChecked = true;
                    break;
            }
        });
        
        // RadioButton変更時にStateを更新
        EnabledButton.IsCheckedChanged += (s, e) =>
        {
            if (EnabledButton.IsChecked == true)
                State = AddonState.Enabled;
        };
        
        DisabledButton.IsCheckedChanged += (s, e) =>
        {
            if (DisabledButton.IsChecked == true)
                State = AddonState.Disabled;
        };
        
        ExcludedButton.IsCheckedChanged += (s, e) =>
        {
            if (ExcludedButton.IsChecked == true)
                State = AddonState.Excluded;
        };
    }
}