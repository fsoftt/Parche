using System.Globalization;
using System.Net;
using System.Text.Json;
using Parche.Core.Models;
using Parche.Core.Net;

namespace Parche.Core.Catalog;

/// <summary>
/// Catálogo TMDB. Acepta clave v3 (<c>api_key</c> en la consulta) o token de lectura v4 (Bearer);
/// el formato se detecta por la forma de la clave. El idioma de los datos es el de la app (es-ES, es-MX, en-US, pt-BR…).
/// </summary>
public sealed class TmdbCatalog : ICatalogProvider
{
    public const string DefaultBase = "https://api.themoviedb.org/3";
    public const string PosterBase = "https://image.tmdb.org/t/p/w342";
    public const string BackdropBase = "https://image.tmdb.org/t/p/w1280";
    public const string StillBase = "https://image.tmdb.org/t/p/w300";

    public static readonly RateLimitSpec Limits = new() { MinIntervalMs = 250, MaxConcurrent = 3, CacheSeconds = 600 };

    private readonly JsonHttp _http;
    private readonly string _apiBase;
    private readonly string _key;
    private readonly bool _bearer;

    public TmdbCatalog(JsonHttp http, string apiKey, string? apiBase = null)
    {
        _http = http;
        _key = (apiKey ?? "").Trim();
        _bearer = IsBearerToken(_key);
        _apiBase = (apiBase ?? DefaultBase).TrimEnd('/');
    }

    public string Name => "TMDB";

    /// <summary>La clave v3 son 32 hex; el token v4 es un JWT ("eyJ…", mucho más largo).</summary>
    public static bool IsBearerToken(string key) =>
        key.StartsWith("eyJ", StringComparison.Ordinal) || key.Length > 40 || key.Contains('.');

    public async Task<HomeFeed> GetHomeAsync(string lang, CancellationToken ct = default)
    {
        var rows = new (string Path, MediaType Type, string Es, string En, string Pt)[]
        {
            ("trending/movie/week", MediaType.Movie, "Películas en tendencia", "Trending movies", "Filmes em alta"),
            ("trending/tv/week", MediaType.Series, "Series en tendencia", "Trending series", "Séries em alta"),
            ("movie/popular", MediaType.Movie, "Películas populares", "Popular movies", "Filmes populares"),
            ("tv/popular", MediaType.Series, "Series populares", "Popular series", "Séries populares"),
            ("movie/top_rated", MediaType.Movie, "Películas mejor valoradas", "Top rated movies", "Filmes mais bem avaliados"),
            ("tv/top_rated", MediaType.Series, "Series mejor valoradas", "Top rated series", "Séries mais bem avaliadas"),
            ("movie/now_playing", MediaType.Movie, "En cines", "Now playing", "Em cartaz"),
        };
        var tasks = rows.Select(r => ListAsync(r.Path, r.Type, lang, null, ct)).ToArray();
        await Task.WhenAll(tasks);
        var sections = new List<MediaSection>();
        for (var i = 0; i < rows.Length; i++)
        {
            if (tasks[i].Result.Count == 0) continue;
            var title = lang.StartsWith("es", StringComparison.OrdinalIgnoreCase) ? rows[i].Es
                : lang.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ? rows[i].Pt : rows[i].En;
            sections.Add(new MediaSection(title, tasks[i].Result));
        }
        var hero = sections.SelectMany(s => s.Items).FirstOrDefault(i => i.Backdrop is not null);
        return new HomeFeed(hero, sections);
    }

    public async Task<IReadOnlyList<MediaItem>> SearchAsync(string query, string lang, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<MediaItem>();
        var json = await GetAsync("search/multi", lang, ct, ("query", query.Trim()), ("include_adult", "false"));
        return json.Arr("results")
            .Where(r => r.Str("media_type") is "movie" or "tv")
            .Select(r => ParseItem(r, r.Str("media_type") == "tv" ? MediaType.Series : MediaType.Movie))
            .Where(i => i is not null).Select(i => i!).ToList();
    }

    public async Task<MediaDetail> GetDetailAsync(MediaItem item, string lang, CancellationToken ct = default)
    {
        var tmdbId = item.TmdbId ?? await ResolveTmdbIdAsync(item.Type, item.Id, ct);
        return await FetchDetailAsync(item.Type, tmdbId, lang, ct);
    }

    public async Task<MediaDetail> GetDetailAsync(MediaType type, string id, string lang, CancellationToken ct = default)
    {
        var tmdbId = int.TryParse(id, out var n) ? n : await ResolveTmdbIdAsync(type, id, ct);
        return await FetchDetailAsync(type, tmdbId, lang, ct);
    }

    public async Task<Season> GetSeasonAsync(MediaDetail show, int seasonNumber, string lang, CancellationToken ct = default)
    {
        var tvId = show.TmdbId ?? await ResolveTmdbIdAsync(MediaType.Series, show.Id, ct);
        var json = await GetAsync($"tv/{tvId}/season/{seasonNumber}", lang, ct);
        return ParseSeason(json, seasonNumber);
    }

    public async Task<IReadOnlyList<Genre>> GetGenresAsync(MediaType type, string lang, CancellationToken ct = default)
    {
        var json = await GetAsync($"genre/{Seg(type)}/list", lang, ct);
        return json.Arr("genres")
            .Select(g => new Genre(g.Str("id") ?? "", g.Str("name") ?? ""))
            .Where(g => g.Id.Length > 0 && g.Name.Length > 0).ToList();
    }

    public Task<IReadOnlyList<MediaItem>> GetByGenreAsync(MediaType type, Genre genre, int page, string lang, CancellationToken ct = default) =>
        ListAsync($"discover/{Seg(type)}", type, lang, page, ct, ("with_genres", genre.Id), ("sort_by", "popularity.desc"));

    // ---------- internos ----------

    private static string Seg(MediaType t) => t == MediaType.Series ? "tv" : "movie";

    private async Task<IReadOnlyList<MediaItem>> ListAsync(string path, MediaType type, string lang, int? page, CancellationToken ct,
        params (string, string)[] extra)
    {
        var q = page is { } p ? extra.Append(("page", Math.Max(1, p).ToString(CultureInfo.InvariantCulture))).ToArray() : extra;
        var json = await GetAsync(path, lang, ct, q);
        return json.Arr("results").Select(r => ParseItem(r, type)).Where(i => i is not null).Select(i => i!).ToList();
    }

    private async Task<JsonElement> GetAsync(string path, string? lang, CancellationToken ct, params (string Key, string Value)[] query)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(lang)) parts.Add("language=" + Uri.EscapeDataString(lang));
        foreach (var (k, v) in query) parts.Add(k + "=" + Uri.EscapeDataString(v).Replace("%2C", ",").Replace("%2F", "/"));
        if (!_bearer) parts.Add("api_key=" + Uri.EscapeDataString(_key));
        var url = $"{_apiBase}/{path}?{string.Join("&", parts)}";
        IReadOnlyDictionary<string, string>? headers = _bearer
            ? new Dictionary<string, string> { ["Authorization"] = "Bearer " + _key } : null;
        return await _http.GetAsync(url, Limits, headers, ct: ct);
    }

    private async Task<int> ResolveTmdbIdAsync(MediaType type, string imdbId, CancellationToken ct)
    {
        if (!imdbId.StartsWith("tt", StringComparison.Ordinal))
            throw new InvalidOperationException("El título no tiene id de IMDb ni de TMDB.");
        var json = await GetAsync($"find/{Uri.EscapeDataString(imdbId)}", null, ct, ("external_source", "imdb_id"));
        var results = json.Arr(type == MediaType.Series ? "tv_results" : "movie_results").FirstOrDefault();
        return results.ValueKind == JsonValueKind.Object && results.Int("id") is { } id
            ? id : throw new HttpRequestException("TMDB no conoce " + imdbId, null, HttpStatusCode.NotFound);
    }

    private async Task<MediaDetail> FetchDetailAsync(MediaType type, int tmdbId, string lang, CancellationToken ct)
    {
        var json = await GetAsync($"{Seg(type)}/{tmdbId}", lang, ct,
            ("append_to_response", "external_ids,credits,videos"), ("include_video_language", lang.Split('-')[0] + ",en,null"));
        var item = ParseItem(json, type) ?? throw new InvalidOperationException("Ficha de TMDB sin título.");
        var imdb = json.Prop("external_ids") is { } ext ? ext.Str("imdb_id") : null;
        imdb ??= json.Str("imdb_id");

        var seasons = new List<Season>();
        if (type == MediaType.Series)
        {
            foreach (var s in json.Arr("seasons"))
            {
                var n = s.Int("season_number");
                if (n is null) continue;
                seasons.Add(new Season(n.Value, s.Str("name") ?? CinemetaCatalog.SeasonTitle(n.Value), Array.Empty<Episode>()));
            }
            seasons = seasons.OrderBy(s => s.Number == 0 ? int.MaxValue : s.Number).ToList();
            // Carga los episodios de la primera temporada para que la ficha se pinte completa.
            var first = seasons.FirstOrDefault(s => s.Number > 0) ?? seasons.FirstOrDefault();
            if (first is not null)
            {
                try
                {
                    var loaded = ParseSeason(await GetAsync($"tv/{tmdbId}/season/{first.Number}", lang, ct), first.Number);
                    seasons[seasons.IndexOf(first)] = loaded with { Title = first.Title };
                }
                catch (HttpRequestException) { /* los episodios se piden luego con GetSeasonAsync */ }
            }
        }

        var trailer = json.Prop("videos")?.Arr("results")
            .Where(v => v.Str("site") == "YouTube" && v.Str("key") is not null)
            .OrderByDescending(v => v.Str("type") == "Trailer").ThenByDescending(v => v.Bool("official"))
            .Select(v => v.Str("key")).FirstOrDefault();

        return new MediaDetail
        {
            Id = imdb ?? "", TmdbId = tmdbId, Type = type, Title = item.Title, Year = item.Year,
            Poster = item.Poster, Backdrop = item.Backdrop, Description = item.Description, Rating = item.Rating,
            Genres = json.Arr("genres").Select(g => g.Str("name")).Where(n => n is not null).Select(n => n!).ToList(),
            Runtime = json.Int("runtime") ?? json.Arr("episode_run_time").Select(e => (int?)e.GetInt32()).FirstOrDefault(),
            Cast = json.Prop("credits")?.Arr("cast").Select(c => c.Str("name")).Where(n => n is not null).Take(8).Select(n => n!).ToList()
                   ?? new List<string>(),
            Seasons = seasons,
            Trailer = trailer is null ? null : "https://www.youtube.com/watch?v=" + trailer,
        };
    }

    internal static Season ParseSeason(JsonElement json, int number)
    {
        var episodes = json.Arr("episodes").Select(e => new Episode
        {
            Season = e.Int("season_number") ?? number,
            Number = e.Int("episode_number") ?? 0,
            Title = e.Str("name") ?? $"Episodio {e.Int("episode_number")}",
            Overview = e.Str("overview"),
            Thumbnail = e.Str("still_path") is { } s ? StillBase + s : null,
            Released = DateTime.TryParse(e.Str("air_date"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null,
        }).OrderBy(e => e.Number).ToList();
        return new Season(number, json.Str("name") ?? CinemetaCatalog.SeasonTitle(number), episodes);
    }

    internal static MediaItem? ParseItem(JsonElement r, MediaType type)
    {
        var title = r.Str("title") ?? r.Str("name");
        if (title is null || r.Int("id") is not { } id) return null;
        var date = r.Str("release_date") ?? r.Str("first_air_date");
        var vote = r.Dbl("vote_average");
        return new MediaItem
        {
            Id = "",
            TmdbId = id,
            Type = type,
            Title = title,
            Year = CinemetaCatalog.ParseYear(date),
            Poster = r.Str("poster_path") is { } p ? PosterBase + p : null,
            Backdrop = r.Str("backdrop_path") is { } b ? BackdropBase + b : null,
            Description = r.Str("overview"),
            Rating = vote is > 0 ? Math.Round(vote.Value, 1) : null,
        };
    }
}
