using Microsoft.UI.Xaml;

namespace Parche.App.WinUI;

/// <summary>Aplicación WinUI 3 que arranca la app MAUI en Windows (sin empaquetar).</summary>
public partial class App : MauiWinUIApplication
{
    public App()
    {
        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
