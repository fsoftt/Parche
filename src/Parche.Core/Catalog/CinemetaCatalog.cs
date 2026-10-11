using System.Globalization;
using System.Text.Json;
using Parche.Core.Models;
using Parche.Core.Net;

namespace Parche.Core.Catalog;

/// <summary>Catálogo Cinemeta (addon oficial de Stremio, sin clave). Solo devuelve datos en inglés.</summary>
public sealed class CinemetaCatalog(JsonHttp http, string? catalogBase = null, string? metaBase = null) : ICatalogProvider
{
    public const string DefaultCatalogBase = "https://v3-cinemeta.strem.io";
    public const string DefaultMetaBase = "https://cinemeta-live.strem.io";
    /// <summary>Elementos por página del catálogo (el paso de <c>skip</c>).</summary>
    public const int PageSize = 50;

    public static readonly RateLimitSpec Limits = new() { MinIntervalMs = 300, MaxConcurrent = 2, CacheSeconds = 600 };

    private readonly string _catalogBase = (catalogBase ?? DefaultCatalogBase).TrimEnd('/');
    private readonly string _metaBase = (metaBase ?? DefaultMetaBase).TrimEnd('/');

    public string Name => "Cinemeta";

    public async Task<HomeFeed> GetHomeAsync(string lang, CancellationToken ct = default)
    {
        var movies = CatalogAsync(MediaType.Movie, null, 0, ct);
        var series = CatalogAsync(MediaType.Series, null, 0, ct);
        await Task.WhenAll(movies, series);
        var sections = new List<MediaSection>
        {
            new("Películas populares", movies.Result),
            new("Series populares", series.Result),
        };
        sections.RemoveAll(s => s.Items.Count == 0);
        var hero = sections.SelectMany(s => s.Items).FirstOrDefault(i => i.Backdrop is not null);
        return new HomeFeed(hero, sections);
    }

    public async Task<IReadOnlyList<MediaItem>> SearchAsync(string query, string lang, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<MediaItem>();
        var q = Uri.EscapeDataString(query.Trim());
        var movies = ListAsync($"{_catalogBase}/catalog/movie/top/search={q}.json", MediaType.Movie, ct);
        var series = ListAsync($"{_catalogBase}/catalog/series/top/search={q}.json", MediaType.Series, ct);
        await Task.WhenAll(movies, series);
        return movies.Result.Concat(series.Result).ToList();
    }

    public Task<MediaDetail> GetDetailAsync(MediaItem item, string lang, CancellationToken ct = default) =>
        item.HasImdbId
            ? GetDetailAsync(item.Type, item.Id, lang, ct)
            : throw new InvalidOperationException("Cinemeta necesita el id de IMDb del título.");

    public async Task<MediaDetail> GetDetailAsync(MediaType type, string id, string lang, CancellationToken ct = default)
    {
        var json = await http.GetAsync($"{_metaBase}/meta/{type.ToStremio()}/{Uri.EscapeDataString(id)}.json", Limits, ct: ct);
        var meta = json.Prop("meta") ?? throw new InvalidOperationException("Cinemeta no devolvió ficha para " + id);
        return ParseDetail(meta, type);
    }

    public async Task<Season> GetSeasonAsync(MediaDetail show, int seasonNumber, string lang, CancellationToken ct = default)
    {
        var cached = show.Seasons.FirstOrDefault(s => s.Number == seasonNumber);
        if (cached is { Episodes.Count: > 0 }) return cached;
        // La ficha ya viene en caché de respuestas (10 min), así que esto no repite la petición.
        var full = await GetDetailAsync(MediaType.Series, show.Id, lang, ct);
        return full.Seasons.FirstOrDefault(s => s.Number == seasonNumber) ?? new Season(seasonNumber, SeasonTitle(seasonNumber), Array.Empty<Episode>());
    }

    public async Task<IReadOnlyList<Genre>> GetGenresAsync(MediaType type, string lang, CancellationToken ct = default)
    {
        var json = await http.GetAsync($"{_catalogBase}/manifest.json", Limits, ct: ct);
        var cat = json.Arr("catalogs").FirstOrDefault(c => c.Str("id") == "top" && c.Str("type") == type.ToStremio());
        var genre = cat.ValueKind == JsonValueKind.Object ? cat.Arr("extra").FirstOrDefault(e => e.Str("name") == "genre") : default;
        return genre.ValueKind == JsonValueKind.Object
            ? genre.StrArr("options").Select(g => new Genre(g, g)).ToList()
            : Array.Empty<Genre>();
    }

    public Task<IReadOnlyList<MediaItem>> GetByGenreAsync(MediaType type, Genre genre, int page, string lang, CancellationToken ct = default) =>
        CatalogAsync(type, genre.Name, Math.Max(0, page - 1) * PageSize, ct);

    private Task<IReadOnlyList<MediaItem>> CatalogAsync(MediaType type, string? genre, int skip, CancellationToken ct)
    {
        var extra = new List<string>();
        if (genre is not null) extra.Add("genre=" + Uri.EscapeDataString(genre));
        if (skip > 0) extra.Add("skip=" + skip);
        var path = extra.Count == 0 ? "" : "/" + string.Join("&", extra);
        return ListAsync($"{_catalogBase}/catalog/{type.ToStremio()}/top{path}.json", type, ct);
    }

    private async Task<IReadOnlyList<MediaItem>> ListAsync(string url, MediaType type, CancellationToken ct)
    {
        var json = await http.GetAsync(url, Limits, ct: ct);
        return json.Arr("metas").Select(m => ParseItem(m, type)).Where(i => i is not null).Select(i => i!).ToList();
    }

    internal static MediaItem? ParseItem(JsonElement m, MediaType fallbackType)
    {
        var title = m.Str("name");
        if (title is null) return null;
        return new MediaItem
        {
            Id = m.Str("imdb_id") ?? m.Str("id") ?? "",
            TmdbId = m.Int("moviedb_id"),
            Type = m.Str("type") is { } t ? MediaTypeExtensions.FromStremio(t) : fallbackType,
            Title = title,
            Year = ParseYear(m.Str("year") ?? m.Str("releaseInfo")),
            Poster = m.Str("poster"),
            Backdrop = m.Str("background"),
            Description = m.Str("description"),
            Rating = m.Dbl("imdbRating"),
            Genres = Genres(m),
        };
    }

    internal static MediaDetail ParseDetail(JsonElement m, MediaType type)
    {
        var item = ParseItem(m, type) ?? throw new InvalidOperationException("Ficha de Cinemeta sin título.");
        var trailer = m.Arr("trailers").Select(t => t.Str("source")).FirstOrDefault(s => s is not null);
        return new MediaDetail
        {
            Id = item.Id, TmdbId = item.TmdbId, Type = item.Type, Title = item.Title, Year = item.Year,
            Poster = item.Poster, Backdrop = item.Backdrop, Description = item.Description, Rating = item.Rating,
            Genres = item.Genres,
            Runtime = ParseRuntime(m.Str("runtime")),
            Cast = m.StrArr("cast").Take(8).ToList(),
            Seasons = item.Type == MediaType.Series ? ParseSeasons(m) : Array.Empty<Season>(),
            Trailer = trailer is null ? null : "https://www.youtube.com/watch?v=" + trailer,
        };
    }

    private static IReadOnlyList<string> Genres(JsonElement m)
    {
        var g = m.StrArr("genres").ToList();
        return g.Count > 0 ? g : m.StrArr("genre").ToList();
    }

    private static List<Season> ParseSeasons(JsonElement m)
    {
        var episodes = new List<Episode>();
        foreach (var v in m.Arr("videos"))
        {
            var season = v.Int("season");
            var number = v.Int("episode") ?? v.Int("number");
            if (season is null || number is null) continue;
            episodes.Add(new Episode
            {
                Season = season.Value,
                Number = number.Value,
                Title = v.Str("name") ?? v.Str("title") ?? $"Episodio {number}",
                Overview = v.Str("overview") ?? v.Str("description"),
                Thumbnail = v.Str("thumbnail"),
                Released = DateTime.TryParse(v.Str("released"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null,
            });
        }
        // Temporadas ascendentes; la 0 (especiales) al final.
        return episodes.GroupBy(e => e.Season)
            .OrderBy(g => g.Key == 0 ? int.MaxValue : g.Key)
            .Select(g => new Season(g.Key, SeasonTitle(g.Key), g.OrderBy(e => e.Number).ToList()))
            .ToList();
    }

    internal static string SeasonTitle(int n) => n == 0 ? "Especiales" : $"Temporada {n}";

    internal static int? ParseYear(string? s)
    {
        if (s is null || s.Length < 4) return null;
        return int.TryParse(s.AsSpan(0, 4), out var y) ? y : null;
    }

    internal static int? ParseRuntime(string? s)
    {
        if (s is null) return null;
        var digits = new string(s.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : null;
    }
}
