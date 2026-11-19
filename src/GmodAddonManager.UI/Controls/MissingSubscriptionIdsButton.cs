using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GmodAddonManager.UI.Services;

namespace GmodAddonManager.UI.Controls;

public sealed class MissingSubscriptionIdsButton : Button
{
    private string ids = string.Empty;

    public MissingSubscriptionIdsButton()
    {
        IsVisible = false;
        Content = L.Get("GamAssetFile.CopyMissingIds");
        Click += OnCopy;
    }

    protected override Type StyleKeyOverride => typeof(Button);

    internal void SetIds(IReadOnlyList<string> missingIds)
    {
        ids = string.Join(Environment.NewLine, missingIds);
        IsVisible = ids.Length > 0;
    }

    private async void OnCopy(object? sender, RoutedEventArgs args)
    {
        try
        {
            if (ids.Length == 0) return;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                Content = L.Get("GamAssetFile.MissingIdsCopyFailed");
                return;
            }
            await clipboard.SetTextAsync(ids);
            Content = L.Get("GamAssetFile.MissingIdsCopied");
        }
        catch (Exception ex)
        {
            SafeFileLogger.TryLogException("MissingSubscriptionIdsButton.OnCopy", ex);
            Content = L.Get("GamAssetFile.MissingIdsCopyFailed");
        }
    }
}
