using Parche.Core.Addons;
using Parche.Core.Library;
using Parche.Core.Models;

namespace Parche.App.Services;

public static class Routes
{
    public const string Detail = "detail";
    public const string Streams = "streams";
    public const string Player = "player";
}

public static class Nav
{
    /// <param name="autoPlay">Abre enseguida el selector de fuentes (botón Reproducir de la portada).</param>
    public static Task ToDetailAsync(MediaItem item, bool autoPlay = false) =>
        Shell.Current.GoToAsync(Routes.Detail, new ShellNavigationQueryParameters { ["item"] = item, ["autoplay"] = autoPlay });

    public static Task ToStreamsAsync(MediaDetail detail, Episode? episode)
    {
        var query = new ShellNavigationQueryParameters { ["detail"] = detail };
        if (episode is not null) query["episode"] = episode;
        return Shell.Current.GoToAsync(Routes.Streams, query);
    }

    /// <param name="item">Título que se reproduce (para "Continuar viendo").</param>
    /// <param name="episode">Episodio, en series.</param>
    /// <param name="fallbacks">Fuentes a probar, en orden, si la elegida no responde.</param>
    /// <param name="localFile">Video descargado que se reproduce sin red (en vez de la fuente).</param>
    public static Task ToPlayerAsync(StreamCandidate candidate, IReadOnlyList<SubtitleTrack> subtitles, string title,
        MediaItem item, Episode? episode, IReadOnlyList<StreamCandidate>? fallbacks = null, string? localFile = null)
    {
        var query = new ShellNavigationQueryParameters
        {
            ["candidate"] = candidate,
            ["subtitles"] = subtitles,
            ["title"] = title,
            ["item"] = item,
            ["fallbacks"] = fallbacks ?? Array.Empty<StreamCandidate>(),
        };
        if (episode is not null) query["episode"] = episode;
        if (localFile is not null) query["localFile"] = localFile;
        return Shell.Current.GoToAsync(Routes.Player, query);
    }

    /// <summary>Reproduce una descarga terminada desde el archivo local, con "Continuar viendo" y subtítulos guardados.</summary>
    public static Task ToOfflinePlayerAsync(DownloadItem download)
    {
        Episode? episode = download.Season is { } s && download.Episode is { } n
            ? new Episode { Season = s, Number = n, Title = download.EpisodeTitle ?? "" }
            : null;
        var title = episode is null
            ? download.Item.Title
            : $"{download.Item.Title} · {Localization.LocalizationManager.T("Detail_EpisodeLabel", episode.Season, episode.Number, episode.Title)}";
        return ToPlayerAsync(download.Source, download.LocalSubtitles, title, download.Item, episode, null, download.FilePath);
    }

    public static bool IsTv => DeviceInfo.Idiom == DeviceIdiom.TV;
}
