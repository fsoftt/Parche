using Microsoft.UI.Windowing;

namespace Parche.App;

/// <summary>Pantalla completa de la ventana en Windows. El reproductor arranca en ventana: se alterna con F o el botón.</summary>
public static class FullScreen
{
    private static AppWindow? Window =>
        (Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window)?.AppWindow;

    /// <summary>La ventana ocupa toda la pantalla.</summary>
    public static bool IsActive => Window?.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    /// <summary>Al abrir el reproductor no se fuerza pantalla completa en escritorio (el usuario la pide con F).</summary>
    public static void Enter() { }

    public static void Exit() => Set(false);

    public static void Toggle() => Set(!IsActive);

    private static void Set(bool on)
    {
        if (Window is not { } window) return;
        window.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
    }
}
