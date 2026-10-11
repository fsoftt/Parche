using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Parche.App.Localization;
using Parche.App.Plugins;
using Parche.App.Services;
using Parche.Core.Library;
using Parche.Core.Models;

namespace Parche.App.ViewModels;
/// <summary>Fila de la lista de episodios.</summary>
/// <summary>Fila de la lista de episodios; el icono y la insignia de descarga se actualizan en sitio.</summary>
public sealed partial class EpisodeRow(Episode episode, string label, string? overview, string? thumbnail) : ObservableObject
{
    public Episode Episode { get; } = episode;
    public string Label { get; } = label;
    public string? Overview { get; } = overview;
    public string? Thumbnail { get; } = thumbnail;

    [ObservableProperty]
    private string _downloadIcon = "download.png";

    /// <summary>"45%", "II" (pausada)… junto al icono; vacío si no hay descarga o ya terminó.</summary>
    [ObservableProperty]
    private string _downloadBadge = "";
}

public partial class DetailViewModel : BaseViewModel, IQueryAttributable
{
    private readonly EngineProvider _engines;
    private readonly MyListService _myList;
    private readonly StreamResolvers _resolvers;
    private readonly DownloadService _downloads;
    private readonly Dictionary<int, Season> _seasons = new();
    private int _loadVersion;
    private bool _autoPlay;

    public DetailViewModel(EngineProvider engines, MyListService myList, StreamResolvers resolvers, DownloadService downloads)
    {
        _engines = engines;
        _myList = myList;
        _resolvers = resolvers;
        _downloads = downloads;
    }

    [ObservableProperty]
    private MediaItem? _item;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Backdrop), nameof(Meta), nameof(IsSeries), nameof(ShowMovieDownload), nameof(CastLine), nameof(HasTrailer), nameof(DownloadText), nameof(DownloadIcon))]
    private MediaDetail? _detail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MyListText))]
    private bool _inMyList;

    [ObservableProperty]
    private bool _isLoadingEpisodes;

    /// <summary>Texto del botón de lista, localizado.</summary>
    public string MyListText => LocalizationManager.T(InMyList ? "Detail_InMyList" : "Common_MyList");

    public ObservableCollection<ChipOption> SeasonChips { get; } = new();
    public ObservableCollection<EpisodeRow> Episodes { get; } = new();

    public string Title => Detail?.Title ?? Item?.Title ?? "";
    public string? Backdrop => Detail?.Backdrop ?? Item?.Wide ?? Detail?.Poster;
    public bool IsSeries => (Detail?.Type ?? Item?.Type) == MediaType.Series;
    public bool HasTrailer => !string.IsNullOrWhiteSpace(Detail?.Trailer);

    /// <summary>Línea de metadatos: "2023 · ★ 8.4 · 2 h 46 min · Drama, Aventura".</summary>
    public string Meta => Format.Join(
        (Detail?.Year ?? Item?.Year)?.ToString(),
        Format.Rating(Detail?.Rating ?? Item?.Rating),
        Format.Runtime(Detail?.Runtime),
        (Detail?.Genres ?? Item?.Genres) is { Count: > 0 } g ? string.Join(", ", g.Take(3)) : null);

    /// <summary>Hay al menos un addon de fuentes; sin ninguno se muestra cómo agregar uno y no se ofrece descargar.</summary>
    public bool HasAddons => _engines.Engine.HasStreamAddons;
    public bool NoAddons => !HasAddons;
    public bool ShowMovieDownload => HasAddons && !IsSeries;

    [RelayCommand]
    private Task OpenSettingsAsync() => Shell.Current.GoToAsync("//settings");

    public string CastLine => Detail?.Cast is { Count: > 0 } c ? LocalizationManager.T("Detail_Cast", string.Join(", ", c.Take(6))) : "";

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        // Shell puede volver a aplicar la consulta al regresar a la página: solo reacciona ante un título nuevo.
        if (query.TryGetValue("item", out var o) && o is MediaItem item && item != Item)
        {
            _autoPlay = query.TryGetValue("autoplay", out var a) && a is true;
            Item = item;
            Detail = null;
            _seasons.Clear();
            SeasonChips.Clear();
            Episodes.Clear();
            InMyList = _myList.Contains(item);
            _ = LoadAsync();
        }
    }

    private Task LoadAsync()
    {
        var version = ++_loadVersion;
        return RunAsync(async () =>
        {
            var detail = await _engines.Engine.GetDetailAsync(Item!);
            if (version != _loadVersion) return;
            Detail = detail;
            if (detail.Type == MediaType.Movie && HasAddons) _ = PreloadBestAsync(detail);
            _seasons.Clear();
            foreach (var s in detail.Seasons) _seasons[s.Number] = s;
            SeasonChips.Clear();
            foreach (var s in detail.Seasons) SeasonChips.Add(new ChipOption(s.Title, s));
            if (SeasonChips.Count > 0) await SelectSeasonAsync(SeasonChips[0]);

            if (_autoPlay)
            {
                _autoPlay = false;
                await PlayAsync();
            }
        });
    }

    /// <summary>
    /// Mientras el usuario lee el detalle, pide las fuentes (quedan en caché para el selector) y empieza a conectar
    /// con la mejor. Así, al tocar Reproducir, arranca en segundos en vez de empezar desde cero.
    /// </summary>
    private async Task PreloadBestAsync(MediaDetail detail)
    {
        try
        {
            var streams = await _engines.Engine.GetStreamsAsync(detail);
            if (streams.FirstOrDefault() is { NeedsResolver: true } best) await _resolvers.PreloadAsync(best);
        }
        catch (Exception) { } // sin precarga se arranca igual, solo más lento
    }

    [RelayCommand]
    private Task RetryAsync() => Item is null ? Task.CompletedTask : LoadAsync();

    /// <summary>Película: abre el selector de fuentes. Serie: reproduce el primer episodio de la temporada elegida.</summary>
    [RelayCommand]
    private async Task PlayAsync()
    {
        if (Detail is null) return;
        if (!IsSeries)
        {
            await Nav.ToStreamsAsync(Detail, null);
            return;
        }
        var first = Episodes.FirstOrDefault();
        if (first is not null) await Nav.ToStreamsAsync(Detail, first.Episode);
    }

    [RelayCommand]
    private Task PlayEpisodeAsync(EpisodeRow row) => Detail is null ? Task.CompletedTask : Nav.ToStreamsAsync(Detail, row.Episode);

    /// <summary>Cambia de temporada; los episodios se piden al catálogo solo la primera vez.</summary>
    [RelayCommand]
    private async Task SelectSeasonAsync(ChipOption option)
    {
        if (option.Value is not Season season || Detail is null) return;
        foreach (var c in SeasonChips) c.IsSelected = ReferenceEquals(c, option);

        if (!_seasons.TryGetValue(season.Number, out var loaded)) loaded = season;
        if (loaded.Episodes.Count == 0)
        {
            IsLoadingEpisodes = true;
            try
            {
                loaded = await _engines.Engine.GetSeasonAsync(Detail, season.Number);
                _seasons[season.Number] = loaded;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Error = ex.Message;
                return;
            }
            finally { IsLoadingEpisodes = false; }
        }

        // El usuario pudo elegir otra temporada mientras cargaba.
        if (!option.IsSelected) return;
        Episodes.Clear();
        foreach (var e in loaded.Episodes)
            Episodes.Add(new EpisodeRow(e, LocalizationManager.T("Detail_EpisodeLabel", e.Season, e.Number, e.Title), e.Overview, e.Thumbnail));
        RefreshDownloads();
    }

    // ── Descargas ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Texto del botón Descargar de la película según el estado de su descarga.</summary>
    public string DownloadText => DownloadLabel(MovieDownload());

    public string DownloadIcon => MovieDownload() is { State: DownloadState.Completed } ? "check.png" : "download.png";

    private DownloadItem? MovieDownload() => Detail is { IsSeries: false } d ? _downloads.Find(d.ToItem(), null) : null;

    private static string DownloadLabel(DownloadItem? d) => d?.State switch
    {
        null => LocalizationManager.T("Downloads_Download"),
        DownloadState.Completed => LocalizationManager.T("Downloads_Done"),
        DownloadState.Downloading when d.BytesTotal > 0 => $"{d.Fraction * 100:0}%",
        DownloadState.Downloading or DownloadState.Queued => LocalizationManager.T("Downloads_Queued"),
        DownloadState.Paused => LocalizationManager.T("Downloads_Paused"),
        _ => LocalizationManager.T("Downloads_Failed"),
    };

    /// <summary>Empieza a recibir cambios de descargas mientras la página está visible.</summary>
    public void Attach()
    {
        _downloads.Updated -= OnDownloadUpdated;
        _downloads.Updated += OnDownloadUpdated;
        RefreshDownloads();
    }

    public void Detach() => _downloads.Updated -= OnDownloadUpdated;

    private void OnDownloadUpdated(object? sender, DownloadItem item) => RefreshDownloads();

    private void RefreshDownloads()
    {
        OnPropertyChanged(nameof(DownloadText));
        OnPropertyChanged(nameof(DownloadIcon));
        OnPropertyChanged(nameof(HasAddons));
        OnPropertyChanged(nameof(NoAddons));
        OnPropertyChanged(nameof(ShowMovieDownload));
        if (Detail is null || !IsSeries) return;
        var series = Detail.ToItem();
        foreach (var row in Episodes)
        {
            var d = _downloads.Find(series, row.Episode);
            row.DownloadIcon = d?.State == DownloadState.Completed ? "check.png" : "download.png";
            row.DownloadBadge = d?.State switch
            {
                DownloadState.Downloading when d.BytesTotal > 0 => $"{d.Fraction * 100:0}%",
                DownloadState.Downloading or DownloadState.Queued => "…",
                DownloadState.Paused => "II",
                DownloadState.Failed => "!",
                _ => "",
            };
        }
    }

    [RelayCommand]
    private Task DownloadMovieAsync() => RequestDownloadAsync(null);

    [RelayCommand]
    private Task DownloadEpisodeAsync(EpisodeRow row) => RequestDownloadAsync(row.Episode);

    /// <summary>
    /// Descargar: terminada, la reproduce; en curso, abre la lista de descargas; pausada, la reanuda; si no existe
    /// (o falló), pide las fuentes, toma la mejor según el ranking y deja las siguientes como respaldo.
    /// </summary>
    private async Task RequestDownloadAsync(Episode? episode)
    {
        if (Detail is null || _preparingDownload) return;
        var media = Detail.ToItem();
        var existing = _downloads.Find(media, episode);
        switch (existing?.State)
        {
            case DownloadState.Completed:
                if (_downloads.FindPlayable(media, episode?.Season, episode?.Number) is { } playable)
                {
                    await Nav.ToOfflinePlayerAsync(playable);
                    return;
                }
                await _downloads.RemoveAsync(existing.Id); // el archivo ya no está: se descarga de nuevo
                break;
            case DownloadState.Paused:
                _downloads.Resume(existing.Id);
                return;
            case DownloadState.Queued or DownloadState.Downloading:
                try { await Shell.Current.GoToAsync("//mylist"); } catch (Exception) { }
                return;
        }

        _preparingDownload = true;
        Error = null;
        try
        {
            await RequestNotificationsAsync();
            var engine = _engines.Engine;
            var streams = await engine.GetStreamsAsync(Detail, episode);
            IReadOnlyList<SubtitleTrack> subtitles;
            try { subtitles = await engine.GetSubtitlesAsync(Detail, episode); }
            catch (Exception) { subtitles = Array.Empty<SubtitleTrack>(); }
            if (!_downloads.Enqueue(media, episode, streams, subtitles))
                Error = LocalizationManager.T("Downloads_NoSources");
        }
        catch (Exception ex) { Error = ex.Message; }
        finally
        {
            _preparingDownload = false;
            RefreshDownloads();
        }
    }

    private bool _preparingDownload;

    /// <summary>Android 13+: pide permiso para mostrar el progreso en la notificación. Si se niega, se descarga igual.</summary>
    private static async Task RequestNotificationsAsync()
    {
#if ANDROID
        try
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;
            if (await Permissions.CheckStatusAsync<Permissions.PostNotifications>() != PermissionStatus.Granted)
                await Permissions.RequestAsync<Permissions.PostNotifications>();
        }
        catch (Exception) { }
#else
        await Task.CompletedTask;
#endif
    }

    [RelayCommand]
    private void ToggleMyList()
    {
        if (Item is null) return;
        InMyList = _myList.Toggle(Detail?.ToItem() ?? Item);
    }

    [RelayCommand]
    private async Task OpenTrailerAsync()
    {
        if (Detail?.Trailer is not { Length: > 0 } url) return;
        try { await Launcher.Default.OpenAsync(url); }
        catch (Exception) { /* sin app para abrir el enlace */ }
    }
}
