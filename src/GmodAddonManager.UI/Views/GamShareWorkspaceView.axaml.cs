using Avalonia.Controls;

namespace GmodAddonManager.UI.Views;

public partial class GamShareWorkspaceView : UserControl
{
    public bool IncludeMemos => IncludeMemoCheckBox.IsChecked == true;

    public GamShareWorkspaceView()
    {
        InitializeComponent();
    }
}
