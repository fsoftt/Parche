using Android.Content.PM;
using AndroidX.Core.View;

namespace Parche.App;

/// <summary>Modo inmersivo para el reproductor: horizontal en celular y sin barras del sistema.</summary>
public static class FullScreen
{
    public static void Enter()
    {
        if (Platform.CurrentActivity is not { Window: { } window } activity) return;
        if (!Services.Nav.IsTv) activity.RequestedOrientation = ScreenOrientation.SensorLandscape;

        var controller = WindowCompat.GetInsetsController(window, window.DecorView)!;
        controller.SystemBarsBehavior = WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe;
        controller.Hide(WindowInsetsCompat.Type.SystemBars());
    }

    public static void Exit()
    {
        if (Platform.CurrentActivity is not { Window: { } window } activity) return;
        if (!Services.Nav.IsTv) activity.RequestedOrientation = ScreenOrientation.Unspecified;

        WindowCompat.GetInsetsController(window, window.DecorView)!.Show(WindowInsetsCompat.Type.SystemBars());
    }
}
