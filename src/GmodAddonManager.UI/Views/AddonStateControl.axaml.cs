using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GmodAddonManager.Core.Models;
using System;

namespace GmodAddonManager.UI.Views;

public partial class AddonStateControl : UserControl
{
    private IDisposable? _stateSubscription;

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

    // Proxy property for compatibility with existing bindings.
    public new bool IsEnabled
    {
        get => State == AddonState.Enabled;
        set
        {
            if (value)
            {
                State = AddonState.Enabled;
            }
        }
    }

    public bool IsDisabled
    {
        get => State == AddonState.Disabled;
        set
        {
            if (value)
            {
                State = AddonState.Disabled;
            }
        }
    }

    public bool IsExcluded
    {
        get => State == AddonState.Excluded;
        set
        {
            if (value)
            {
                State = AddonState.Excluded;
            }
        }
    }

    public AddonStateControl()
    {
        InitializeComponent();
        DataContext = this;

        EnabledButton.IsCheckedChanged += OnEnabledButtonCheckedChanged;
        DisabledButton.IsCheckedChanged += OnDisabledButtonCheckedChanged;
        ExcludedButton.IsCheckedChanged += OnExcludedButtonCheckedChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _stateSubscription?.Dispose();
        _stateSubscription = this.GetObservable(StateProperty).Subscribe(UpdateButtons);
        UpdateButtons(State);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _stateSubscription?.Dispose();
        _stateSubscription = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void UpdateButtons(AddonState state)
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
    }

    private void OnEnabledButtonCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (EnabledButton.IsChecked == true)
        {
            State = AddonState.Enabled;
        }
    }

    private void OnDisabledButtonCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (DisabledButton.IsChecked == true)
        {
            State = AddonState.Disabled;
        }
    }

    private void OnExcludedButtonCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (ExcludedButton.IsChecked == true)
        {
            State = AddonState.Excluded;
        }
    }
}
