using System.Net;
using Parche.Core.Addons;
using Parche.Core.Catalog;
using Parche.Core.Models;
using Parche.Core.Net;
using Parche.Core.Settings;

namespace Parche.Core;

/// <summary>
/// Fachada del núcleo: catálogo (TMDB/Cinemeta), fuentes de addons compatibles con el protocolo de addons de Stremio, ranking y subtítulos.
/// Se construye con unos <see cref="AppSettings"/>; si cambian los ajustes, crear otra instancia.
/// </summary>
public sealed class ParcheEngine
{
    private readonly AppSettings _settings;
    private readonly JsonHttp _http;
    private readonly AddonClient _addons;
    private readonly SubtitlesClient _subtitles;
    private readonly IReadOnlyList<IStreamSourceProvider> _providers;
    private readonly Func<StreamCandidate, bool>? _candidateFilter;

    public ParcheEngine(AppSettings settings, HttpClient httpClient, ICatalogProvider? catalog = null,
        RateLimiter? limiter = null, ResponseCache? cache = null, IEnumerable<IStreamSourceProvider>? providers = null,
        Func<StreamCandidate, bool>? candidateFilter = null)
    {
        _settings = settings.Normalize();
        _providers = providers?.ToList() ?? new List<IStreamSourceProvider>();
        _candidateFilter = candidateFilter;
        _http = new JsonHttp(httpClient, limiter, cache);
        _addons = new AddonClient(_http);
        _subtitles = new SubtitlesClient(_http);
        Catalog = catalog ?? CreateCatalog(_settings, _http);
    }

    public ICatalogProvider Catalog { get; }
    public AppSettings Settings => _settings;
    private string Lang => _settings.AppLanguage;

    /// <summary>Addons de fuentes vigentes: los de cada <see cref="IStreamSourceProvider"/> y los que agregó el usuario, normalizados y sin repetidos.</summary>
    public IReadOnlyList<string> EffectiveStreamAddons() =>
        _providers.SelectMany(p => p.GetAddonUrls(_settings))
            .Concat(_settings.StreamAddons)
            .Where(a => !string.IsNullOrWhiteSpace(a)).Select(AddonClient.NormalizeBase)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Hay al menos un addon de fuentes (del usuario o de un plugin); sin ninguno la lista de fuentes sale siempre vacía.</summary>
    public bool HasStreamAddons => EffectiveStreamAddons().Count > 0;

    /// <summary>Elige el catálogo según los ajustes (Auto: TMDB con clave, si no Cinemeta; TMDB con 401 cae a Cinemeta).</summary>
    public static ICatalogProvider CreateCatalog(AppSettings s, JsonHttp http)
    {
        var cinemeta = new CinemetaCatalog(http);
        var tmdb = s.CatalogProvider != CatalogProviderKind.Cinemeta && s.HasTmdbKey ? new TmdbCatalog(http, s.TmdbApiKey!) : null;
        return new CompositeCatalog(tmdb, cinemeta);
    }

    // ---------- catálogo ----------

    public Task<HomeFeed> GetHomeAsync(CancellationToken ct = default) => Catalog.GetHomeAsync(Lang, ct);
    public Task<IReadOnlyList<MediaItem>> SearchAsync(string query, CancellationToken ct = default) => Catalog.SearchAsync(query, Lang, ct);
    public Task<MediaDetail> GetDetailAsync(MediaItem item, CancellationToken ct = default) => Catalog.GetDetailAsync(item, Lang, ct);
    public Task<MediaDetail> GetDetailAsync(MediaType type, string id, CancellationToken ct = default) => Catalog.GetDetailAsync(type, id, Lang, ct);
    public Task<Season> GetSeasonAsync(MediaDetail show, int seasonNumber, CancellationToken ct = default) => Catalog.GetSeasonAsync(show, seasonNumber, Lang, ct);
    public Task<IReadOnlyList<Genre>> GetGenresAsync(MediaType type, CancellationToken ct = default) => Catalog.GetGenresAsync(type, Lang, ct);
    public Task<IReadOnlyList<MediaItem>> GetByGenreAsync(MediaType type, Genre genre, int page = 1, CancellationToken ct = default) => Catalog.GetByGenreAsync(type, genre, page, Lang, ct);

    // ---------- streams ----------

    public async Task<IReadOnlyList<StreamCandidate>> GetStreamsAsync(MediaItem item, Episode? episode = null, CancellationToken ct = default)
    {
        var imdb = item.HasImdbId ? item.Id : (await Catalog.GetDetailAsync(item, Lang, ct)).Id;
        return await GetStreamsAsync(item.Type, imdb, episode, ct, item.Year);
    }

    public Task<IReadOnlyList<StreamCandidate>> GetStreamsAsync(MediaDetail detail, Episode? episode = null, CancellationToken ct = default) =>
        detail.HasImdbId
            ? GetStreamsAsync(detail.Type, detail.Id, episode, ct, detail.Year)
            : throw new InvalidOperationException("El título no tiene id de IMDb; los addons lo necesitan.");

    /// <summary>
    /// Consulta todos los addons en paralelo, une, deduplica y ordena. Un addon caído no rompe el resto. El filtro de
    /// candidatos (si hay) descarta los que la app no sabe reproducir, p. ej. los que necesitan un resolvedor ausente.
    /// </summary>
    /// <param name="year">Año de la película: descarta fuentes cuyo archivo dice otro año (remakes que el addon mezcla).</param>
    public async Task<IReadOnlyList<StreamCandidate>> GetStreamsAsync(MediaType type, string imdbId, Episode? episode,
        CancellationToken ct = default, int? year = null)
    {
        var id = VideoId(type, imdbId, episode);
        var addons = EffectiveStreamAddons();
        if (addons.Count == 0) return Array.Empty<StreamCandidate>();

        var tasks = addons.Select(a => _addons.GetStreamsAsync(a, type.ToStremio(), id, ct)).ToList();
        try { await Task.WhenAll(tasks); } catch { /* se evalúa task a task */ }
        ct.ThrowIfCancellationRequested();

        var ok = tasks.Where(t => t.IsCompletedSuccessfully).SelectMany(t => t.Result).ToList();
        if (ok.Count == 0 && tasks.All(t => t.IsFaulted))
            throw tasks[0].Exception!.InnerException ?? tasks[0].Exception!;

        if (_candidateFilter is not null) ok = ok.Where(_candidateFilter).ToList();
        var merged = ok.GroupBy(c => c.DedupKey)
            .Select(g => g.OrderByDescending(c => c.IsInstant).ThenByDescending(c => c.Popularity ?? 0).First())
            .ToList();
        if (type == MediaType.Movie && year is { } y) merged = StreamRanker.DropOtherYears(merged, y).ToList();
        return StreamRanker.Rank(merged, _settings.PreferredLanguages, _settings.PreferredMaxQuality);
    }

    // ---------- subtítulos ----------

    /// <summary>Subtítulos filtrados por <see cref="AppSettings.SubtitleLanguages"/>; los errores del addon devuelven lista vacía.</summary>
    public async Task<IReadOnlyList<SubtitleTrack>> GetSubtitlesAsync(MediaItem item, Episode? episode = null, CancellationToken ct = default)
    {
        try
        {
            var imdb = item.HasImdbId ? item.Id : (await Catalog.GetDetailAsync(item, Lang, ct)).Id;
            return await SubtitlesFor(item.Type, imdb, episode, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return Array.Empty<SubtitleTrack>();
        }
    }

    public async Task<IReadOnlyList<SubtitleTrack>> GetSubtitlesAsync(MediaDetail detail, Episode? episode = null, CancellationToken ct = default)
    {
        if (!detail.HasImdbId) return Array.Empty<SubtitleTrack>();
        try { return await SubtitlesFor(detail.Type, detail.Id, episode, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return Array.Empty<SubtitleTrack>();
        }
    }

    private async Task<IReadOnlyList<SubtitleTrack>> SubtitlesFor(MediaType type, string imdbId, Episode? episode, CancellationToken ct)
    {
        var addons = _settings.SubtitleAddons.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var id = VideoId(type, imdbId, episode);
        var tasks = addons.Select(a => _subtitles.GetSubtitlesAsync(a, type.ToStremio(), id, ct)).ToList();
        try { await Task.WhenAll(tasks); } catch { /* se evalúa addon a addon: uno caído no afecta a los demás */ }
        ct.ThrowIfCancellationRequested();

        // Se unen en el orden de los ajustes y se descartan los repetidos; si todos fallan, se reproduce sin subtítulos.
        var all = tasks.Where(t => t.IsCompletedSuccessfully).SelectMany(t => t.Result)
            .GroupBy(t => t.Url, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        return SubtitlesClient.Filter(all, _settings.SubtitleLanguages);
    }

    // ---------- reproducción ----------

    /// <summary>
    /// Petición de reproducción para un candidato con enlace directo. Los que solo tienen identificador (sin URL) los
    /// convierte en enlace un <c>IStreamResolver</c> de la app antes de llegar aquí.
    /// </summary>
    public static PlaybackRequest CreatePlayback(StreamCandidate stream, IReadOnlyList<SubtitleTrack>? subtitles = null, string? title = null) =>
        string.IsNullOrEmpty(stream.Url)
            ? throw new InvalidOperationException("El candidato no tiene enlace directo; hace falta un resolvedor de fuentes.")
            : new PlaybackRequest
            {
                Url = stream.Url, Headers = stream.Headers, Kind = PlaybackRequest.Guess(stream.Url),
                Subtitles = subtitles ?? Array.Empty<SubtitleTrack>(), Title = title,
            };

    /// <summary>"tt123" para películas; "tt123:S:E" para episodios.</summary>
    public static string VideoId(MediaType type, string imdbId, Episode? episode)
    {
        if (type != MediaType.Series) return imdbId;
        return episode is null
            ? throw new ArgumentException("Para una serie hay que indicar el episodio.", nameof(episode))
            : AddonClient.EpisodeId(imdbId, episode.Season, episode.Number);
    }
}
