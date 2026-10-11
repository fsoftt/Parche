using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Parche.App.Controls;
using Parche.App.Localization;
using Parche.App.Plugins;
#if ANDROID
using Parche.App.Platforms.Android;
#elif WINDOWS
using Parche.App.Platforms.Windows;
#endif
using Parche.App.Services;
using Parche.App.ViewModels;
using Parche.App.Views;

namespace Parche.App;

public static partial class MauiProgram
{
    /// <summary>
    /// Punto de extensión para fuentes que viven fuera de este repositorio. No tiene cuerpo aquí, así que la llamada
    /// desaparece al compilar salvo que <c>Parche.Plugins.props</c> (ver <c>ParchePluginsProps</c> en Parche.App.csproj)
    /// agregue un archivo que lo implemente: registra <c>IStreamSourceProvider</c>, <c>IStreamResolver</c>,
    /// <c>IDownloadResolver</c> e <c>IPluginSettingsSection</c>.
    /// </summary>
    static partial void AddExternalSources(IServiceCollection services);

    public static MauiApp CreateMauiApp()
    {
        LocalizationManager.Initialize();

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            })
            .ConfigureMauiHandlers(h =>
            {
                h.AddHandler<NativeVideoView, NativeVideoViewHandler>();
                h.AddHandler<TvCard, TvCardHandler>();
            });

        var s = builder.Services;

        // Red y servicios: un solo HttpClient compartido por el catálogo, los addons y los subtítulos.
        s.AddSingleton(_ =>
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(HttpDefaults.UserAgent);
            return http;
        });
        s.AddSingleton<SettingsService>();
        s.AddSingleton<EngineProvider>();
        s.AddSingleton<StreamResolvers>();
        s.AddSingleton<DownloadResolvers>();
        s.AddSingleton<MyListService>();
        s.AddSingleton<WatchProgressService>();
        s.AddSingleton<DownloadService>();

        // Fuentes externas (plugins): después de los servicios del núcleo.
        AddExternalSources(s);

        // Pestañas: una instancia (conservan su estado). Detalle/fuentes/reproductor: una por navegación.
        s.AddSingleton<HomeViewModel>().AddSingleton<HomePage>();
        s.AddSingleton<SearchViewModel>().AddSingleton<SearchPage>();
        s.AddSingleton<CategoriesViewModel>().AddSingleton<CategoriesPage>();
        s.AddSingleton<MyListViewModel>().AddSingleton<MyListPage>();
        s.AddSingleton<SettingsViewModel>().AddSingleton<SettingsPage>();
        s.AddTransient<DetailViewModel>().AddTransient<DetailPage>();
        s.AddTransient<StreamPickerViewModel>().AddTransient<StreamPickerPage>();
        s.AddTransient<PlayerViewModel>().AddTransient<PlayerPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }
}
