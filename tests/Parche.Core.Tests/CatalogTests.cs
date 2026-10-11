using System.Net;
using Parche.Core.Catalog;
using Parche.Core.Models;

namespace Parche.Core.Tests;

public class CinemetaCatalogTests
{
    private static (CinemetaCatalog Catalog, FakeHandler Handler) Make()
    {
        var handler = FakeHandler.Routes(
            ("/catalog/series/top/search=", "cinemeta-series-search.json"),
            ("/catalog/movie/top/search=", "cinemeta-movie-search.json"),
            ("/catalog/movie/top", "cinemeta-movie-top.json"),
            ("/catalog/series/top", "cinemeta-series-search.json"),
            ("/meta/series/", "cinemeta-meta-series.json"),
            ("/manifest.json", "cinemeta-manifest.json"));
        return (new CinemetaCatalog(Env.Json(handler)), handler);
    }

    [Fact]
    public async Task Home_has_movie_and_series_rows_and_a_hero()
    {
        var (c, h) = Make();
        var home = await c.GetHomeAsync("es-ES");
        Assert.Equal(2, home.Sections.Count);
        Assert.Contains("https://v3-cinemeta.strem.io/catalog/movie/top.json", h.Urls);
        Assert.Contains("https://v3-cinemeta.strem.io/catalog/series/top.json", h.Urls);
        var first = home.Sections[0].Items[0];
        Assert.Equal("tt6933238", first.Id);
        Assert.Equal(MediaType.Movie, first.Type);
        Assert.Equal("Unabomber", first.Title);
        Assert.Equal(2026, first.Year);
        Assert.Equal(1492640, first.TmdbId);
        Assert.Equal(6.2, first.Rating);
        Assert.Equal(new[] { "Biography", "Crime", "Drama" }, first.Genres);
        Assert.Equal("https://images.metahub.space/background/medium/tt6933238/img", first.Backdrop);
        Assert.NotNull(home.Hero);
    }

    [Fact]
    public async Task Search_encodes_the_query_and_queries_both_types()
    {
        var (c, h) = Make();
        var res = await c.SearchAsync("breaking bad", "es-ES");
        Assert.Contains("https://v3-cinemeta.strem.io/catalog/series/top/search=breaking%20bad.json", h.Urls);
        Assert.Contains("https://v3-cinemeta.strem.io/catalog/movie/top/search=breaking%20bad.json", h.Urls);
        Assert.Contains(res, i => i.Type == MediaType.Series && i.Id == "tt0903747" && i.Title == "Breaking Bad");
        Assert.Equal(2008, res.First(i => i.Id == "tt0903747").Year);
    }

    [Fact]
    public async Task Detail_groups_videos_into_seasons_with_specials_last()
    {
        var (c, h) = Make();
        var d = await c.GetDetailAsync(MediaType.Series, "tt0903747", "es-ES");
        Assert.Contains("https://cinemeta-live.strem.io/meta/series/tt0903747.json", h.Urls);
        Assert.Equal("Breaking Bad", d.Title);
        Assert.Equal("tt0903747", d.Id);
        Assert.Equal(1396, d.TmdbId);
        Assert.Equal(49, d.Runtime);
        Assert.Equal(3, d.Cast.Count);
        Assert.Equal("https://www.youtube.com/watch?v=VFkjBy2b50Q", d.Trailer);
        Assert.Equal(new[] { 1, 2, 5, 0 }, d.Seasons.Select(s => s.Number));
        Assert.Equal("Temporada 1", d.Seasons[0].Title);
        Assert.Equal("Especiales", d.Seasons[^1].Title);
        Assert.Equal(new[] { 1, 2, 3, 4 }, d.Seasons[0].Episodes.Select(e => e.Number));
        var ep = d.Seasons[0].Episodes[0];
        Assert.Equal(1, ep.Season);
        Assert.False(string.IsNullOrEmpty(ep.Title));
        Assert.NotNull(ep.Overview);
        Assert.NotNull(ep.Thumbnail);
        Assert.Equal(2008, ep.Released?.Year);
        Assert.True(d.IsSeries);
    }

    [Fact]
    public async Task GetSeason_returns_episodes_from_cached_meta()
    {
        var (c, h) = Make();
        var d = await c.GetDetailAsync(MediaType.Series, "tt0903747", "es-ES");
        var s = await c.GetSeasonAsync(d with { Seasons = Array.Empty<Season>() }, 2, "es-ES");
        Assert.Equal(4, s.Episodes.Count);
        Assert.Equal(1, h.Urls.Count(u => u.Contains("/meta/series/")));
    }

    [Fact]
    public async Task Detail_of_item_without_imdb_id_fails_clearly()
    {
        var (c, _) = Make();
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.GetDetailAsync(new MediaItem { Title = "x", TmdbId = 1 }, "en-US"));
    }

    [Fact]
    public async Task Genres_come_from_the_manifest_top_catalog()
    {
        var (c, _) = Make();
        var movie = await c.GetGenresAsync(MediaType.Movie, "en-US");
        var series = await c.GetGenresAsync(MediaType.Series, "en-US");
        Assert.Contains(new Genre("Action", "Action"), movie);
        Assert.DoesNotContain(movie, g => g.Name == "Reality-TV");
        Assert.Contains(series, g => g.Name == "Reality-TV");
    }

    [Theory]
    [InlineData(1, "https://v3-cinemeta.strem.io/catalog/movie/top/genre=Sci-Fi.json")]
    [InlineData(3, "https://v3-cinemeta.strem.io/catalog/movie/top/genre=Sci-Fi&skip=100.json")]
    public async Task By_genre_pages_with_skip(int page, string expected)
    {
        var (c, h) = Make();
        await c.GetByGenreAsync(MediaType.Movie, new Genre("Sci-Fi", "Sci-Fi"), page, "en-US");
        Assert.Contains(expected, h.Urls);
    }
}

public class TmdbCatalogTests
{
    private const string V3Key = "0123456789abcdef0123456789abcdef";
    private const string V4Token = "eyJhbGciOiJIUzI1NiJ9.eyJhdWQiOiJ4In0.signature";

    private static (TmdbCatalog Catalog, FakeHandler Handler) Make(string key = V3Key)
    {
        var handler = FakeHandler.Routes(
            ("/search/multi", "tmdb-search-multi.json"),
            ("/find/tt1375666", "tmdb-find-imdb.json"),
            ("/movie/27205", "tmdb-movie-detail.json"),
            ("/tv/1396/season/1", "tmdb-tv-season1.json"),
            ("/tv/1396", "tmdb-tv-detail.json"),
            ("/genre/movie/list", "tmdb-genres-movie.json"),
            ("/discover/movie", "tmdb-movie-popular.json"),
            ("/movie/popular", "tmdb-movie-popular.json"),
            ("/trending/", "tmdb-movie-popular.json"),
            ("/tv/", "tmdb-movie-popular.json"),
            ("/movie/", "tmdb-movie-popular.json"));
        return (new TmdbCatalog(Env.Json(handler), key), handler);
    }

    [Fact]
    public async Task Home_requests_all_rows_with_language_and_maps_items()
    {
        var (c, h) = Make();
        var home = await c.GetHomeAsync("es-MX");
        Assert.Equal(7, home.Sections.Count);
        Assert.Equal("Películas en tendencia", home.Sections[0].Title);
        foreach (var path in new[] { "trending/movie/week", "trending/tv/week", "movie/popular", "tv/popular", "movie/top_rated", "tv/top_rated", "movie/now_playing" })
            Assert.Contains(h.Urls, u => u.Contains("/3/" + path + "?language=es-MX"));
        var item = home.Sections[2].Items[0];
        Assert.Equal(27205, item.TmdbId);
        Assert.Equal("", item.Id);
        Assert.False(item.HasImdbId);
        Assert.Equal("Origen", item.Title);
        Assert.Equal(2010, item.Year);
        Assert.Equal(8.4, item.Rating);
        Assert.Equal("https://image.tmdb.org/t/p/w342/oYuLEt3zVCKq57qu2F8dT7NIa6f.jpg", item.Poster);
        Assert.Equal("https://image.tmdb.org/t/p/w1280/s3TBrRGB1iav7gFOCNx3H31MoES.jpg", item.Backdrop);
        var bare = home.Sections[2].Items[1];
        Assert.Null(bare.Poster);
        Assert.Null(bare.Year);
        Assert.Null(bare.Rating);
        Assert.NotNull(home.Hero);
    }

    [Fact]
    public async Task V3_key_goes_in_query_and_v4_token_in_bearer_header()
    {
        var (c3, h3) = Make(V3Key);
        await c3.GetGenresAsync(MediaType.Movie, "es-ES");
        Assert.Contains("api_key=" + V3Key, h3.Urls.Single());
        Assert.Null(h3.Requests.Single().Headers.Authorization);

        var (c4, h4) = Make(V4Token);
        await c4.GetGenresAsync(MediaType.Movie, "es-ES");
        Assert.DoesNotContain("api_key", h4.Urls.Single());
        Assert.Equal("Bearer " + V4Token, h4.Requests.Single().Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Search_skips_people_and_maps_types()
    {
        var (c, h) = Make();
        var res = await c.SearchAsync("breaking bad", "es-ES");
        Assert.Contains(h.Urls, u => u.Contains("query=breaking%20bad"));
        Assert.Equal(2, res.Count);
        Assert.Equal(MediaType.Series, res[0].Type);
        Assert.Equal("Breaking Bad", res[0].Title);
        Assert.Equal(2008, res[0].Year);
        Assert.Equal(MediaType.Movie, res[1].Type);
    }

    [Fact]
    public async Task Movie_detail_resolves_imdb_cast_trailer_and_runtime()
    {
        var (c, h) = Make();
        var d = await c.GetDetailAsync(new MediaItem { Title = "Origen", TmdbId = 27205, Type = MediaType.Movie }, "es-ES");
        Assert.Contains(h.Urls, u => u.Contains("/movie/27205?") && u.Contains("append_to_response=external_ids,credits,videos"));
        Assert.Equal("tt1375666", d.Id);
        Assert.Equal(27205, d.TmdbId);
        Assert.Equal(148, d.Runtime);
        Assert.Equal(8, d.Cast.Count);
        Assert.Equal("Leonardo DiCaprio", d.Cast[0]);
        Assert.Equal("https://www.youtube.com/watch?v=YoHD9XEInc0", d.Trailer);
        Assert.Equal(new[] { "Acción", "Ciencia ficción", "Aventura" }, d.Genres);
        Assert.Empty(d.Seasons);
    }

    [Fact]
    public async Task Detail_by_imdb_id_uses_find_endpoint()
    {
        var (c, h) = Make();
        var d = await c.GetDetailAsync(new MediaItem { Id = "tt1375666", Title = "Inception", Type = MediaType.Movie }, "en-US");
        Assert.Contains(h.Urls, u => u.Contains("/find/tt1375666?") && u.Contains("external_source=imdb_id"));
        Assert.Equal("tt1375666", d.Id);
    }

    [Fact]
    public async Task Tv_detail_lists_seasons_and_loads_first_season_episodes()
    {
        var (c, h) = Make();
        var d = await c.GetDetailAsync(MediaType.Series, "1396", "es-ES");
        Assert.Equal("tt0903747", d.Id);
        Assert.Equal(45, d.Runtime);
        Assert.Equal(new[] { 1, 2, 0 }, d.Seasons.Select(s => s.Number));
        Assert.Equal(new[] { 1, 2, 3 }, d.Seasons[0].Episodes.Select(e => e.Number));
        Assert.Empty(d.Seasons[1].Episodes);
        Assert.Equal("Piloto", d.Seasons[0].Episodes[0].Title);
        Assert.Equal("https://image.tmdb.org/t/p/w300/a.jpg", d.Seasons[0].Episodes[0].Thumbnail);
        Assert.Equal(new DateTime(2008, 1, 20), d.Seasons[0].Episodes[0].Released?.Date);
        Assert.Null(d.Seasons[0].Episodes[2].Released);
        Assert.Contains(h.Urls, u => u.Contains("/tv/1396/season/1?"));
    }

    [Fact]
    public async Task GetSeason_requests_one_season_lazily()
    {
        var (c, h) = Make();
        var show = new MediaDetail { Title = "Breaking Bad", TmdbId = 1396, Type = MediaType.Series, Id = "tt0903747" };
        var s = await c.GetSeasonAsync(show, 1, "es-ES");
        Assert.Equal(3, s.Episodes.Count);
        Assert.Equal(1, s.Number);
        Assert.Single(h.Urls);
    }

    [Fact]
    public async Task Genres_and_discover()
    {
        var (c, h) = Make();
        var g = await c.GetGenresAsync(MediaType.Movie, "es-ES");
        Assert.Equal(new Genre("28", "Acción"), g[0]);
        var items = await c.GetByGenreAsync(MediaType.Movie, g[0], 2, "es-ES");
        Assert.Contains(h.Urls, u => u.Contains("/discover/movie?") && u.Contains("with_genres=28") && u.Contains("page=2"));
        Assert.Equal(2, items.Count);
    }
}

public class CompositeCatalogTests
{
    private static JsonHttpPair Make(Func<HttpRequestMessage, HttpResponseMessage?> tmdb)
    {
        var tmdbHandler = new FakeHandler(tmdb);
        var cineHandler = FakeHandler.Routes(("/catalog/movie/top", "cinemeta-movie-top.json"), ("/catalog/series/top", "cinemeta-series-search.json"));
        return new JsonHttpPair(tmdbHandler, cineHandler);
    }

    private sealed record JsonHttpPair(FakeHandler Tmdb, FakeHandler Cine)
    {
        public CompositeCatalog Composite(bool withTmdb = true) =>
            new(withTmdb ? new TmdbCatalog(Env.Json(Tmdb), "0123456789abcdef0123456789abcdef") : null, new CinemetaCatalog(Env.Json(Cine)));
    }

    [Fact]
    public async Task Uses_tmdb_when_configured()
    {
        var p = Make(r => FakeHandler.Ok("tmdb-movie-popular.json"));
        var home = await p.Composite().GetHomeAsync("es-ES");
        Assert.Equal(7, home.Sections.Count);
        Assert.Empty(p.Cine.Requests);
    }

    [Fact]
    public async Task Falls_back_to_cinemeta_without_key()
    {
        var p = Make(_ => null);
        var c = p.Composite(withTmdb: false);
        var home = await c.GetHomeAsync("es-ES");
        Assert.Equal("Cinemeta", c.Name);
        Assert.Equal(2, home.Sections.Count);
        Assert.Empty(p.Tmdb.Requests);
    }

    [Fact]
    public async Task Falls_back_to_cinemeta_on_401_and_remembers_it()
    {
        var p = Make(_ => FakeHandler.Raw(Fixture.Read("tmdb-error-401.json"), HttpStatusCode.Unauthorized));
        var c = p.Composite();
        var home = await c.GetHomeAsync("es-ES");
        Assert.Equal(2, home.Sections.Count);
        Assert.True(c.PrimaryRejected);
        var tmdbCalls = p.Tmdb.Requests.Count;
        await c.GetHomeAsync("es-ES");
        Assert.Equal(tmdbCalls, p.Tmdb.Requests.Count);
    }

    [Fact]
    public async Task Other_tmdb_errors_are_not_swallowed()
    {
        var p = Make(_ => FakeHandler.Raw("{}", HttpStatusCode.InternalServerError));
        await Assert.ThrowsAsync<HttpRequestException>(() => p.Composite().GetHomeAsync("es-ES"));
    }
}
