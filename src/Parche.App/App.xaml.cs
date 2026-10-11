using Parche.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Parche.App;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        InitializeComponent();
        UserAppTheme = AppTheme.Dark;
        _services = services;
        // Retoma las descargas pendientes (y el servicio en primer plano) al abrir la app.
        services.GetService<DownloadService>()?.Start();
    }

    // En TV: menú lateral (rail) navegable con D-pad. En celular: pestañas inferiores como Netflix.
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(Nav.IsTv ? new TvShell(_services) : new AppShell(_services));
#if WINDOWS
        // Escritorio: título, tamaño inicial y mínimo razonables (la grilla se adapta al ancho).
        window.Title = "Parche";
        window.MinimumWidth = 800;
        window.MinimumHeight = 560;
        window.Width = 1280;
        window.Height = 800;
#endif
        return window;
    }
}
