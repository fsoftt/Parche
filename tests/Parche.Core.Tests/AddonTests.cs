using System.Text.Json;
using Parche.Core.Addons;
using Parche.Core.Models;
using Parche.Core.Settings;

namespace Parche.Core.Tests;

public class StreamParserTests
{
    private static IReadOnlyList<StreamCandidate> Parse(string fixture) =>
        StreamParser.ParseResponse(JsonDocument.Parse(Fixture.Read(fixture)).RootElement, "addon.test");

    [Theory]
    [InlineData("The.Matrix.1999.2160p.UHD.BluRay.REMUX.HDR.TrueHD.7.1", "remux,hdr,7.1")]
    [InlineData("Night.Of.The.Living.Dead.1968.1080p.BluRay.x264-[YTS.AM].mp4", "bluray")]
    [InlineData("Show.S01E02.1080p.WEB-DL.DDP5.1.H.264", "web,5.1")]
    [InlineData("3D The Matrix 1 1999 (3DHDTV Stereo3D HDTV 2x720p", "tv,3d")]
    [InlineData("Movie.2024.HDCAM.x264", "cam")]
    [InlineData("Origen (2010) 720p Castellano", "")]
    public void Detects_release_tags(string text, string expected) =>
        Assert.Equal(expected, string.Join(",", StreamParser.ReleaseTags(text)));

    [Theory]
    [InlineData("Night.Of.The.Living.Dead.1968.1080p.BluRay.x264-[YTS.AM].mp4", 1968)]
    [InlineData("Night.of.the.Living.Dead.2026.1080p.CAMRip.LAT.DUB.mp4", 2026)]
    [InlineData("Blade.Runner.2049.2017.2160p.UHD", 2017)]
    [InlineData("1917 (2019) 1920x1080 WEB-DL", 2019)]
    [InlineData("La noche de los muertos vivientes [Bluray 720p]", null)]
    public void Parses_release_year(string text, int? expected) => Assert.Equal(expected, StreamParser.ParseYear(text));

    [Fact]
    public void Drops_sources_of_another_year_but_keeps_unknown_ones()
    {
        var c = new[]
        {
            new StreamCandidate { Name = "orig", FileName = "Night.of.the.Living.Dead.1968.1080p.mkv" },
            new StreamCandidate { Name = "remake", FileName = "Night.of.the.Living.Dead.2026.1080p.CAMRip.mp4" },
            new StreamCandidate { Name = "close", FileName = "Night.of.the.Living.Dead.1969.720p.mkv" },
            new StreamCandidate { Name = "noyear", Title = "La noche de los muertos vivientes 720p" },
        };
        Assert.Equal(new[] { "orig", "close", "noyear" }, StreamRanker.DropOtherYears(c, 1968).Select(x => x.Name));
    }

    [Fact]
    public void Parses_reference_streams()
    {
        var s = Parse("addon-streams.json");
        Assert.Equal(4, s.Count);
        var first = s[0];
        Assert.Equal("1080p", first.Quality);
        Assert.InRange(first.SizeBytes!.Value, 2_250_000_000L, 2_260_000_000L); // 2.1 GB
        Assert.Equal(120, first.Popularity);
        Assert.Equal("SiteA", first.Provider);
        Assert.Equal(string.Concat(Enumerable.Repeat("ab", 20)), first.InfoHash);
        Assert.Equal(0, first.FileIdx);
        Assert.Null(first.Url);
        Assert.False(first.IsDirect);
        Assert.True(first.NeedsResolver);
        Assert.Equal(new[] { "udp://hint-a.test:1337/announce", "udp://hint-b.test:80/announce" }, first.ConnectionHints);
        Assert.Equal("Inception.2010.1080p.BluRay.x264.SPANISH-LATINO.mkv", first.FileName);
        Assert.Equal("addon|1080p|BluRay", first.BingeGroup);
        Assert.Contains("es", first.Languages);
        Assert.Contains("es-419", first.Languages);
        Assert.Contains("en", first.Languages);

        Assert.Equal("2160p", s[1].Quality);
        Assert.Equal(new HashSet<string> { "en" }, s[1].Languages);
        Assert.Equal(1, s[1].FileIdx);
        Assert.Equal("720p", s[2].Quality);
        Assert.Equal(700L * 1024 * 1024, s[2].SizeBytes);
        Assert.Equal(new HashSet<string> { "es" }, s[2].Languages);
        Assert.Equal("480p", s[3].Quality);
        Assert.Null(s[3].FileIdx);
    }

    [Fact]
    public void Parses_direct_streams_and_skips_unplayable_entries()
    {
        var s = Parse("addon-streams-direct.json");
        Assert.Equal(2, s.Count);
        var instant = s[0];
        Assert.True(instant.IsDirect);
        Assert.True(instant.IsInstant);
        Assert.False(instant.NeedsResolver);
        Assert.StartsWith("https://addon.test/direct/", instant.Url);
        Assert.Equal("1080p", instant.Quality);
        Assert.Equal("file.mkv", instant.FileName);
        Assert.Equal("Parche", instant.Headers["User-Agent"]);
        Assert.Equal("https://addon.test/", instant.Headers["Referer"]);

        Assert.True(s[1].IsDirect);
        Assert.False(s[1].IsInstant);
        Assert.Equal("2160p", s[1].Quality);
        Assert.Equal(18L << 30, s[1].SizeBytes);
    }

    [Theory]
    [InlineData("💾 2.1 GB", 2254857830L)]
    [InlineData("💾 700 MB", 734003200L)]
    [InlineData("💾 1,5 GB", 1610612736L)]
    [InlineData("sin tamaño", null)]
    public void Parses_sizes(string text, long? expected)
    {
        var size = StreamParser.ParseSize(text);
        if (expected is null) Assert.Null(size);
        else Assert.InRange(size!.Value, expected.Value - 1_000_000, expected.Value + 1_000_000);
    }

    [Theory]
    [InlineData("Addon\n4k", "2160p")]
    [InlineData("Movie.2160p.HDR", "2160p")]
    [InlineData("Movie.UHD.BluRay", "2160p")]
    [InlineData("x 1080p y", "1080p")]
    [InlineData("Movie.720p.WEB", "720p")]
    [InlineData("Movie.DVDRip", "480p")]
    [InlineData("Movie.CAM", "SD")]
    public void Detects_quality(string text, string expected) => Assert.Equal(expected, StreamParser.DetectQuality(text));
}

public class LanguageDetectionTests
{
    [Theory]
    [InlineData("Movie.2010.1080p.SPANISH-LATINO", "es,es-419")]
    [InlineData("Movie 2010 Castellano 720p", "es")]
    [InlineData("Movie.ESP.1080p", "es")]
    [InlineData("Movie.LAT.WEB-DL", "es-419")]
    [InlineData("Movie.2010.ENG.1080p", "en")]
    [InlineData("Movie 1080p English", "en")]
    [InlineData("Movie 1080p\n🇪🇸", "es")]
    [InlineData("Movie 1080p\n🇲🇽 / 🇦🇷", "es-419")]
    [InlineData("Movie 1080p\n🇬🇧 / 🇺🇸", "en")]
    [InlineData("Movie 1080p\n🇧🇷", "pt")]
    [InlineData("Movie 1080p\n🇵🇹", "pt")]
    [InlineData("Movie.DUAL.1080p", "multi")]
    [InlineData("Multi Audio / 🇪🇸 / 🇬🇧", "multi,es,en")]
    [InlineData("Movie.2010.1080p.BluRay.x264", "en")]
    [InlineData("The.Specialist.1994", "en")]
    public void Detects_languages(string text, string expected) =>
        Assert.True(new HashSet<string>(expected.Split(',')).SetEquals(LanguageCodes.Detect(text)), string.Join(",", LanguageCodes.Detect(text)));

    [Fact]
    public void Preference_matching_treats_base_language_as_regions_too()
    {
        Assert.True(LanguageCodes.Matches("es", "es-419"));
        Assert.False(LanguageCodes.Matches("es-419", "es"));
        Assert.True(LanguageCodes.Matches("en", "en"));
        Assert.False(LanguageCodes.Matches("en", "es"));
    }
}

public class StreamRankerTests
{
    private static StreamCandidate C(string name, string quality = "1080p", string langs = "en", int? popularity = null, long? size = null,
        bool instant = false) => new()
    {
        Name = name, Quality = quality, Languages = new HashSet<string>(langs.Split(',')), Popularity = popularity, SizeBytes = size,
        IsInstant = instant, IsDirect = instant, InfoHash = name,
    };

    private static string[] Order(IEnumerable<StreamCandidate> c) => c.Select(x => x.Name).ToArray();

    [Fact]
    public void Instant_sources_go_first()
    {
        var r = StreamRanker.Rank(new[] { C("a", popularity: 999, langs: "es"), C("b", "720p", "en", instant: true) });
        Assert.Equal(new[] { "b", "a" }, Order(r));
    }

    [Fact]
    public void Language_order_follows_preferences()
    {
        var r = StreamRanker.Rank(new[] { C("en", langs: "en"), C("lat", langs: "es-419"), C("es", langs: "es") },
            new[] { "es-419", "es", "en" });
        Assert.Equal(new[] { "lat", "es", "en" }, Order(r));
    }

    [Fact]
    public void Keeps_every_language_and_puts_preferred_first_on_ties()
    {
        var tied = new[] { C("fr", langs: "fr"), C("es", langs: "es") };
        Assert.Equal(new[] { "es", "fr" }, Order(StreamRanker.Rank(tied)));

        // Más disponibilidad gana aunque el idioma no sea el preferido.
        var popular = new[] { C("es", langs: "es", popularity: 5), C("en", langs: "en", popularity: 100) };
        Assert.Equal(new[] { "en", "es" }, Order(StreamRanker.Rank(popular, new[] { "es" })));
    }

    [Fact]
    public void Multi_audio_ranks_after_explicit_matches_and_before_other_languages()
    {
        var r = StreamRanker.Rank(new[] { C("m", langs: "multi"), C("e", langs: "en"), C("x", langs: "fr") }, new[] { "es", "en" });
        Assert.Equal(new[] { "e", "m", "x" }, Order(r));
    }

    [Fact]
    public void Quality_prefers_highest_within_cap_then_the_rest_above_it()
    {
        var all = new[] { C("4k", "2160p"), C("1080", "1080p"), C("720", "720p"), C("sd", "SD") };
        Assert.Equal(new[] { "1080", "720", "sd", "4k" }, Order(StreamRanker.Rank(all, null, "1080p")));
        Assert.Equal(new[] { "4k", "1080", "720", "sd" }, Order(StreamRanker.Rank(all, null, null)));
        Assert.Equal(new[] { "720", "sd", "1080", "4k" }, Order(StreamRanker.Rank(all, null, "720p")));
    }

    [Fact]
    public void Ties_break_by_popularity_then_size()
    {
        var r = StreamRanker.Rank(new[]
        {
            C("lowpop", popularity: 3, size: 9), C("big", popularity: 50, size: 5_000), C("small", popularity: 50, size: 1_000),
        });
        Assert.Equal(new[] { "big", "small", "lowpop" }, Order(r));
    }

    [Fact]
    public void Popularity_outranks_language_and_quality()
    {
        var r = StreamRanker.Rank(new[]
        {
            C("es720", "720p", "es", popularity: 10), C("en4k", "2160p", "en", popularity: 96), C("en1080", "1080p", "en", popularity: 29),
        });
        Assert.Equal(new[] { "en4k", "en1080", "es720" }, Order(r));
    }
}

public class SubtitlesTests
{
    private static IReadOnlyList<SubtitleTrack> Load() =>
        SubtitlesClient.Parse(JsonDocument.Parse(Fixture.Read("opensubtitles-movie.json")).RootElement);

    [Fact]
    public void Maps_three_letter_codes_to_iso1_and_labels()
    {
        var all = Load();
        Assert.Equal(12, all.Count);
        Assert.Contains(all, t => t.Language == "es" && t.Label == "Español");
        Assert.Contains(all, t => t.Language == "en" && t.Label == "English");
        Assert.Contains(all, t => t.Language == "pt-BR");
        Assert.All(all, t => Assert.Equal("application/x-subrip", t.MimeType));
        Assert.All(all, t => Assert.StartsWith("https://", t.Url));
    }

    [Fact]
    public void Filters_by_wanted_languages_in_preference_order_and_numbers_duplicates()
    {
        var r = SubtitlesClient.Filter(Load(), new[] { "spa", "eng" });
        Assert.Equal(6, r.Count);
        Assert.Equal(new[] { "es", "es", "es", "en", "en", "en" }, r.Select(t => t.Language));
        Assert.Equal(new[] { "Español", "Español 2", "Español 3" }, r.Take(3).Select(t => t.Label));

        var eng = SubtitlesClient.Filter(Load(), new[] { "en" }, perLanguage: 1);
        Assert.Single(eng);
        Assert.Empty(SubtitlesClient.Filter(Load(), new[] { "jpn" }));
    }

    [Theory]
    [InlineData("spa", "es")]
    [InlineData("eng", "en")]
    [InlineData("por", "pt")]
    [InlineData("pob", "pt-BR")]
    [InlineData("fre", "fr")]
    public void Iso_conversion(string three, string two) => Assert.Equal(two, LanguageCodes.ToIso1(three));
}

public class AddonClientTests
{
    [Fact]
    public async Task Requests_the_expected_stream_url()
    {
        var handler = FakeHandler.Routes(("/stream/", "addon-streams.json"));
        var client = new AddonClient(Env.Json(handler));

        var streams = await client.GetStreamsAsync("https://addon.test/config=x|y", "movie", "tt1375666");
        Assert.Equal("https://addon.test/config=x|y/stream/movie/tt1375666.json", Uri.UnescapeDataString(handler.Urls.Single()));
        Assert.Equal(4, streams.Count);
        Assert.Equal("addon.test", streams[0].Addon);

        await client.GetStreamsAsync("stremio://other.test/manifest.json", "series", "tt0903747:1:2");
        Assert.Equal("https://other.test/stream/series/tt0903747:1:2.json", handler.Urls[1]);
    }

    [Theory]
    [InlineData("https://a.test/manifest.json", "https://a.test")]
    [InlineData("https://a.test/", "https://a.test")]
    [InlineData("stremio://a.test/x/manifest.json", "https://a.test/x")]
    public void Normalizes_base_urls(string input, string expected) => Assert.Equal(expected, AddonClient.NormalizeBase(input));
}
