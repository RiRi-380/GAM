using Avalonia;
using Avalonia.Controls;

namespace GmodAddonManager.UI.Controls;

public partial class AddonDetailsControl : UserControl
{
    // Avalonia's physical ScrollContentPresenter uses 50 DIPs per wheel unit.
    // Keep overlay-routed input indistinguishable from a wheel over the panel.
    private const double PhysicalWheelStep = 50;

    public AddonDetailsControl()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Scrolls the outer details surface when an unhandled wheel gesture lands
    /// on the modal overlay or header instead of directly on the side panel.
    /// </summary>
    internal bool ScrollFromUnhandledWheel(Vector wheelDelta)
    {
        if (wheelDelta.Y == 0 ||
            AddonDetailsScrollViewer.Extent.Height <= AddonDetailsScrollViewer.Viewport.Height)
        {
            return false;
        }

        var previousOffset = AddonDetailsScrollViewer.Offset;
        AddonDetailsScrollViewer.Offset = new Vector(
            previousOffset.X,
            previousOffset.Y -
            (wheelDelta.Y * PhysicalWheelStep));
        return AddonDetailsScrollViewer.Offset != previousOffset;
    }
}
