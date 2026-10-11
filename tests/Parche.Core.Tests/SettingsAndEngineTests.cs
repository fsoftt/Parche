using System.Net;
using System.Text.Json;
using Parche.Core.Addons;
using Parche.Core.Catalog;
using Parche.Core.Models;
using Parche.Core.Net;
using Parche.Core.Settings;

namespace Parche.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void Defaults_match_the_spec()
    {
        var s = new AppSettings();
        Assert.Equal(CatalogProviderKind.Auto, s.CatalogProvider);
        Assert.Equal(new[] { "es-419", "es", "en" }, s.PreferredLanguages);
        Assert.Equal("1080p", s.PreferredMaxQuality);
        Assert.Equal(new[] { "https://opensubtitles-v3.strem.io" }, s.SubtitleAddons);
        Assert.Equal(new[] { "spa", "eng" }, s.SubtitleLanguages);
        Assert.Empty(s.StreamAddons); // sin fuentes por defecto: el usuario agrega sus addons
        Assert.Empty(s.Plugins);
        Assert.True(s.IsValid);
    }

    [Fact]
    public void Round_trips_through_json()
    {
        var s = new AppSettings
        {
            CatalogProvider = CatalogProviderKind.Tmdb, TmdbApiKey = "k", AppLanguage = "pt-BR",
            PreferredMaxQuality = "720p", StreamAddons = { "https://a.test/manifest.json" },
            SubtitleLanguages = { "por" }, SubtitleAddons = { "https://subs.test" },
        };
        s.SetPlugin("demo", new DemoPlugin { Name = "x", Count = 3 });
        var json = s.ToJson();
        Assert.Contains("\"Tmdb\"", json);
        var back = AppSettings.FromJson(json);
        Assert.Equal(json, back.ToJson());
        Assert.Equal(new[] { "https://a.test/manifest.json" }, back.StreamAddons);
        Assert.Equal(new[] { "https://opensubtitles-v3.strem.io", "https://subs.test" }, back.SubtitleAddons);
        Assert.Equal(3, back.GetPlugin<DemoPlugin>("demo")!.Count);
        Assert.Null(back.GetPlugin<DemoPlugin>("other"));
    }

    [Fact]
    public void Unknown_properties_survive_load_and_save()
    {
        var s = AppSettings.FromJson("{\"TmdbApiKey\":\"k\",\"Legacy\":{\"A\":1,\"B\":[\"x\"]},\"Flag\":true}");
        Assert.Equal("k", s.TmdbApiKey);
        Assert.Equal(1, s.GetExtra<DemoLegacy>("Legacy")!.A);

        var again = AppSettings.FromJson(s.ToJson());
        Assert.Equal(new[] { "x" }, again.GetExtra<DemoLegacy>("legacy")!.B);
        using var doc = JsonDocument.Parse(again.ToJson());
        Assert.True(doc.RootElement.GetProperty("Flag").GetBoolean());

        // Una vez migrada, la propiedad se puede descartar y no vuelve a salir.
        again.RemoveExtra("Legacy");
        Assert.DoesNotContain("Legacy", again.ToJson());
        Assert.Contains("\"Flag\"", again.ToJson());
    }

    [Fact]
    public void Old_single_subtitle_addon_is_migrated_to_the_list()
    {
        var s = AppSettings.FromJson("{\"SubtitlesAddonUrl\":\"https://my-subs.test\"}");
        Assert.Equal(new[] { "https://my-subs.test" }, s.SubtitleAddons);
        Assert.DoesNotContain("SubtitlesAddonUrl", s.ToJson());

        var untouched = AppSettings.FromJson("{\"SubtitlesAddonUrl\":\"https://opensubtitles-v3.strem.io\"}");
        Assert.Equal(new[] { "https://opensubtitles-v3.strem.io" }, untouched.SubtitleAddons);

        var cleared = AppSettings.FromJson("{\"SubtitleAddons\":[]}");
        Assert.Empty(cleared.SubtitleAddons);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{not json")]
    public void Bad_json_gives_defaults(string? json) => Assert.Equal(new AppSettings().ToJson(), AppSettings.FromJson(json).ToJson());

    [Fact]
    public void Null_collections_in_json_are_repaired()
    {
        var s = AppSettings.FromJson("{\"PreferredLanguages\":null,\"StreamAddons\":null,\"Plugins\":null,\"SubtitleAddons\":null}");
        Assert.Empty(s.StreamAddons);
        Assert.NotNull(s.Plugins);
        Assert.Single(s.SubtitleAddons);
        Assert.Empty(s.PreferredLanguages);
    }

    [Fact]
    public void Validation_reports_problems_in_spanish()
    {
        var s = new AppSettings
        {
            CatalogProvider = CatalogProviderKind.Tmdb, PreferredMaxQuality = "8k",
            StreamAddons = { "no-url" }, SubtitleAddons = { "tampoco" },
            PreferredLanguages = new(),
        };
        var errors = s.Validate();
        Assert.Contains("Falta la clave de TMDB.", errors);
        Assert.Contains(errors, e => e.Contains("Calidad máxima"));
        Assert.Contains(errors, e => e.Contains("no-url"));
        Assert.Contains(errors, e => e.Contains("tampoco"));
        Assert.Contains(errors, e => e.Contains("idioma"));
        Assert.False(s.IsValid);
    }

    private sealed class DemoPlugin
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
    }

    private sealed class DemoLegacy
    {
        public int A { get; set; }
        public List<string> B { get; set; } = new();
    }
}

public class EngineTests
{
    private const string Hash = "abababababababababababababababababababab";

    private static AppSettings WithAddon() => new() { StreamAddons = { "https://addon.test" } };

    private static (ParcheEngine Engine, FakeHandler Handler) Make(AppSettings? settings = null, Func<HttpRequestMessage, HttpResponseMessage?>? extra = null,
        IEnumerable<IStreamSourceProvider>? providers = null, Func<StreamCandidate, bool>? filter = null)
    {
        var routes = FakeHandler.Routes(
            ("opensubtitles-v3.strem.io/subtitles/", "opensubtitles-movie.json"),
            ("addon.test/", "addon-streams.json"),
            ("/catalog/movie/top", "cinemeta-movie-top.json"),
            ("/catalog/series/top", "cinemeta-series-search.json"),
            ("/meta/series/", "cinemeta-meta-series.json"));
        var handler = new FakeHandler(r => extra?.Invoke(r) ?? routes.Dispatch(r));
        var clock = new FakeClock();
        var engine = new ParcheEngine(settings ?? WithAddon(), new HttpClient(handler), null, new RateLimiter(clock, _ => 0), new ResponseCache(clock), providers, filter);
        return (engine, handler);
    }

    private static readonly MediaDetail Movie = new() { Id = "tt1375666", Type = MediaType.Movie, Title = "Inception" };
    private static readonly MediaDetail Show = new() { Id = "tt0903747", Type = MediaType.Series, Title = "Breaking Bad" };

    private sealed class FixedProvider(params string[] urls) : IStreamSourceProvider
    {
        public IEnumerable<string> GetAddonUrls(AppSettings settings) => urls;
    }

    [Fact]
    public async Task Home_uses_cinemeta_without_tmdb_key()
    {
        var (e, h) = Make();
        var home = await e.GetHomeAsync();
        Assert.Equal(2, home.Sections.Count);
        Assert.All(h.Urls, u => Assert.Contains("cinemeta", u));
    }

    [Fact]
    public async Task Streams_are_ranked_by_availability()
    {
        var (e, h) = Make();
        var streams = await e.GetStreamsAsync(Movie);
        Assert.Equal("https://addon.test/stream/movie/tt1375666.json", Uri.UnescapeDataString(h.Urls[0]));
        // De más a menos disponibilidad: 1080p (120), 2160p (45), 720p (8), 480p (2).
        Assert.Equal(new[] { "1080p", "2160p", "720p", "480p" }, streams.Select(s => s.Quality));
    }

    [Fact]
    public async Task Without_any_addon_there_are_no_sources_and_no_requests()
    {
        var (e, h) = Make(new AppSettings());
        Assert.False(e.HasStreamAddons);
        Assert.Empty(await e.GetStreamsAsync(Movie));
        Assert.Empty(h.Urls);
    }

    [Fact]
    public async Task Providers_add_their_addons_before_the_users_and_without_duplicates()
    {
        var s = new AppSettings { StreamAddons = { "https://second.test/manifest.json", " ", "https://addon.test/" } };
        var (e, h) = Make(s, providers: new[] { new FixedProvider("https://addon.test/cfg"), new FixedProvider("https://addon.test") });
        Assert.Equal(new[] { "https://addon.test/cfg", "https://addon.test", "https://second.test" }, e.EffectiveStreamAddons());
        Assert.True(e.HasStreamAddons);
        var streams = await e.GetStreamsAsync(Movie);
        Assert.Equal(4, streams.Count); // los tres addons devuelven lo mismo (dedupe por identificador)
        Assert.Equal(3, h.Urls.Count(u => u.Contains("/stream/movie/")));
    }

    [Fact]
    public async Task A_provider_alone_is_enough_to_have_sources()
    {
        var (e, _) = Make(new AppSettings(), providers: new[] { new FixedProvider("https://addon.test") });
        Assert.NotEmpty(await e.GetStreamsAsync(Movie));
    }

    [Fact]
    public async Task Candidate_filter_drops_what_the_app_cannot_play()
    {
        // Sin resolvedor, los candidatos con identificador pero sin enlace se descartan.
        var (e, _) = Make(filter: c => !c.NeedsResolver);
        Assert.Empty(await e.GetStreamsAsync(Movie));
    }

    [Fact]
    public async Task Series_streams_use_season_and_episode_in_the_id()
    {
        var (e, h) = Make();
        await e.GetStreamsAsync(Show, new Episode { Season = 2, Number = 3, Title = "x" });
        Assert.EndsWith("/stream/series/tt0903747:2:3.json", h.Urls[0]);
        await Assert.ThrowsAsync<ArgumentException>(() => e.GetStreamsAsync(Show));
    }

    [Fact]
    public async Task Streams_from_several_addons_are_merged_and_deduplicated()
    {
        var s = new AppSettings { StreamAddons = { "https://addon.test", "https://second.test" } };
        var (e, h) = Make(s, r => r.RequestUri!.Host == "second.test" ? FakeHandler.Ok("addon-streams.json") : null);
        var streams = await e.GetStreamsAsync(Movie);
        Assert.Equal(4, streams.Count);
        Assert.Equal(2, h.Urls.Count(u => u.Contains("/stream/movie/")));
    }

    [Fact]
    public async Task A_failing_addon_does_not_break_the_others_but_all_failing_throws()
    {
        var s = new AppSettings { StreamAddons = { "https://addon.test", "https://bad.test" } };
        var (e, _) = Make(s, r => r.RequestUri!.Host == "bad.test" ? new HttpResponseMessage(HttpStatusCode.Forbidden) : null);
        Assert.Equal(4, (await e.GetStreamsAsync(Movie)).Count);

        var (e2, _) = Make(s, _ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        await Assert.ThrowsAsync<HttpRequestException>(() => e2.GetStreamsAsync(Movie));
    }

    [Fact]
    public async Task Item_without_imdb_id_is_resolved_through_the_catalog()
    {
        var (_, h) = Make();
        var item = new MediaItem { Title = "Breaking Bad", TmdbId = 1396, Id = "", Type = MediaType.Series };
        var cat = new FakeCatalog(Show);
        var engine = new ParcheEngine(WithAddon(), new HttpClient(h), cat, new RateLimiter(new FakeClock(), _ => 0), new ResponseCache());
        var streams = await engine.GetStreamsAsync(item, new Episode { Season = 1, Number = 1, Title = "x" });
        Assert.NotEmpty(streams);
        Assert.Contains(h.Urls, u => u.EndsWith("/stream/series/tt0903747:1:1.json"));
    }

    [Fact]
    public async Task Subtitles_are_filtered_by_settings()
    {
        var (e, h) = Make(new AppSettings { SubtitleLanguages = new() { "spa" } });
        var subs = await e.GetSubtitlesAsync(Movie);
        Assert.Equal("https://opensubtitles-v3.strem.io/subtitles/movie/tt1375666.json", h.Urls[0]);
        Assert.Equal(3, subs.Count);
        Assert.All(subs, s => Assert.Equal("es", s.Language));
    }

    [Fact]
    public async Task Subtitles_from_several_addons_are_merged_without_duplicates_and_a_failing_one_is_skipped()
    {
        var s = new AppSettings
        {
            SubtitleLanguages = new() { "spa" },
            SubtitleAddons = { "https://subs-b.test", "https://subs-bad.test" },
        };
        var (e, h) = Make(s, r => r.RequestUri!.Host switch
        {
            "subs-bad.test" => new HttpResponseMessage(HttpStatusCode.BadGateway),
            "subs-b.test" => FakeHandler.Ok("opensubtitles-movie.json"),
            _ => null,
        });
        var subs = await e.GetSubtitlesAsync(Movie);
        Assert.Equal(3, h.Urls.Count(u => u.Contains("/subtitles/movie/")));
        Assert.Equal(3, subs.Count); // el mismo archivo en dos addons se cuenta una vez
    }

    [Fact]
    public async Task Subtitle_errors_yield_an_empty_list()
    {
        var (e, _) = Make(null, _ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        Assert.Empty(await e.GetSubtitlesAsync(Movie));
    }

    [Fact]
    public async Task Without_subtitle_addons_there_are_no_subtitles_and_no_requests()
    {
        var (e, h) = Make(new AppSettings { SubtitleAddons = new() });
        Assert.Empty(await e.GetSubtitlesAsync(Movie));
        Assert.Empty(h.Urls);
    }

    [Fact]
    public async Task Season_delegates_to_the_catalog()
    {
        var (e, _) = Make();
        var show = await e.GetDetailAsync(MediaType.Series, "tt0903747");
        Assert.Equal(4, (await e.GetSeasonAsync(show, 1)).Episodes.Count);
    }

    [Fact]
    public void Playback_request_from_direct_candidate_and_rejection_of_unresolved_ones()
    {
        var direct = new StreamCandidate { Url = "https://host/v.m3u8?x=1", IsDirect = true, Headers = new Dictionary<string, string> { ["Referer"] = "r" } };
        var req = ParcheEngine.CreatePlayback(direct, null, "Inception");
        Assert.Equal(StreamKind.Hls, req.Kind);
        Assert.Equal("r", req.Headers["Referer"]);
        Assert.Equal("Inception", req.Title);
        Assert.Throws<InvalidOperationException>(() => ParcheEngine.CreatePlayback(new StreamCandidate { InfoHash = Hash }));
    }

    private sealed class FakeCatalog(MediaDetail detail) : ICatalogProvider
    {
        public string Name => "fake";
        public Task<HomeFeed> GetHomeAsync(string lang, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MediaItem>> SearchAsync(string query, string lang, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MediaDetail> GetDetailAsync(MediaItem item, string lang, CancellationToken ct = default) => Task.FromResult(detail);
        public Task<MediaDetail> GetDetailAsync(MediaType type, string id, string lang, CancellationToken ct = default) => Task.FromResult(detail);
        public Task<Season> GetSeasonAsync(MediaDetail show, int seasonNumber, string lang, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Genre>> GetGenresAsync(MediaType type, string lang, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MediaItem>> GetByGenreAsync(MediaType type, Genre genre, int page, string lang, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
