using System;
using System.Threading.Tasks;
using GmodAddonManager.Core.Models;

namespace GmodAddonManager.UI.Services;

internal static class BulkStateConfirmation
{
    internal static async Task<bool> RunAsync(bool enabled, string title,
        Func<AssetStateChangePreview> preview, Func<AssetStateChangePreview?, Task<bool>> apply,
        IDialogService dialogs)
    {
        if (!enabled) return await apply(null);
        var changed = false;
        while (true)
        {
            var impact = preview();
            var message = impact.On.HasValue
                ? L.Format("Bulk.Impact", impact.Assets, impact.ExclusionsToNeutral, impact.On.Value, impact.Off!.Value)
                : L.Format("Bulk.Unknown", impact.Assets, impact.ExclusionsToNeutral);
            if (impact.GroupDefaults > 0)
                message += Environment.NewLine + L.Format("Bulk.GroupDefaults", impact.GroupDefaults);
            if (changed)
                message = L.Get("Bulk.Changed") + Environment.NewLine + message;
            if (!await dialogs.ShowConfirmAsync(title, message)) return false;
            if (await apply(impact)) return true;
            changed = true;
        }
    }
}
