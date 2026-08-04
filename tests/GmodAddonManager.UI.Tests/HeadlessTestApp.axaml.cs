using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;

[assembly: AvaloniaTestApplication(
    typeof(GmodAddonManager.UI.Tests.HeadlessTestAppBuilder))]

namespace GmodAddonManager.UI.Tests;

public sealed partial class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }
}

public static class HeadlessTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<HeadlessTestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}
