// Native WinUI application entry point; no Minecraft work happens during launch.
using Microsoft.UI.Xaml;

namespace BlockFerry.App.WinUI;

public partial class App : Application
{
    private Window? _window;
    private BlockFerry.App.WinUI.Services.BlockFerryCompositionRoot? _composition;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _composition = BlockFerry.App.WinUI.Services.BlockFerryCompositionRoot.CreateProduction();
        _window = new MainWindow(_composition);
        _window.Activate();
    }
}
