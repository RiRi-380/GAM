namespace GmodAddonManager.UI.ViewModels;

public class FilterOptionViewModel : ViewModelBase
{
    public FilterOptionViewModel(string key, string label)
    {
        Key = key;
        Label = label;
    }

    public string Key { get; }
    public string Label { get; }

    private bool isSelected;
    public bool IsSelected
    {
        get => isSelected;
        set => SetAndRaise(ref isSelected, value);
    }
}
