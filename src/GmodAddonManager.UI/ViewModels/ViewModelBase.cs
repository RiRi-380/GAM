using ReactiveUI;
using System.Runtime.CompilerServices;

namespace GmodAddonManager.UI.ViewModels;

public class ViewModelBase : ReactiveObject
{
    protected void SetAndRaise<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        this.RaiseAndSetIfChanged(ref field, value, propertyName);
    }
}