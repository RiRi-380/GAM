using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using GmodAddonManager.UI.Views;

namespace GmodAddonManager.UI.Tests;

public sealed class SettingsSectionNavigationTests
{
    [AvaloniaFact]
    public void NavigationAlignsEachCardWithTheViewportAfterResizing()
    {
        var dialog = new SettingsDialog();
        dialog.Show();
        try
        {
            var scroller = Assert.IsType<ScrollViewer>(dialog.FindControl<ScrollViewer>("SettingsScrollViewer"));
            var viewport = Assert.Single(scroller.GetVisualDescendants().OfType<ScrollContentPresenter>(),
                presenter => presenter.Content == scroller.Content);
            var buttons = dialog.GetVisualDescendants().OfType<Button>()
                .Where(button => button.Tag is string section && section.EndsWith("Section", StringComparison.Ordinal))
                .ToArray();
            Assert.Equal(4, buttons.Length);

            foreach (var height in new[] { 600, 1000, 480 })
            {
                dialog.Height = height;
                dialog.UpdateLayout();

                foreach (var button in buttons.Reverse())
                {
                    var card = Assert.IsType<Border>(dialog.FindControl<Border>((string)button.Tag!));
                    var center = new Point(button.Bounds.Width / 2, button.Bounds.Height / 2);
                    var clickPoint = button.TranslatePoint(center, dialog)!.Value;
                    dialog.MouseDown(clickPoint, MouseButton.Left, RawInputModifiers.LeftMouseButton);
                    dialog.MouseUp(clickPoint, MouseButton.Left);
                    dialog.UpdateLayout();

                    var top = card.TranslatePoint(default, viewport)!.Value.Y;
                    Assert.True(Math.Abs(top) < 0.01, $"{button.Tag} at window height {height} landed at Y={top}.");
                }
            }
        }
        finally
        {
            dialog.Close();
        }
    }
}
