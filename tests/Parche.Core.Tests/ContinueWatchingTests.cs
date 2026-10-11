using System.Text.Json;
using Parche.Core.Addons;
using Parche.Core.Library;
using Parche.Core.Models;

namespace Parche.Core.Tests;

public class ContinueWatchingTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static MediaItem Movie(string id, int? tmdb = null) => new() { Id = id, TmdbId = tmdb, Type = MediaType.Movie, Title = id };

    private static WatchEntry E(MediaItem item, long pos, long dur = 6_000_000, int minutes = 0, int? season = null, int? episode = null) => new()
    {
        Item = item, PositionMs = pos, DurationMs = dur, UpdatedUtc = T0.AddMinutes(minutes), Season = season, Episode = episode,
    };

    [Fact]
    public void Ignores_less_than_a_minute_and_removes_finished()
    {
        var cw = new ContinueWatching();
        var m = Movie("tt1");
        Assert.False(cw.Report(E(m, 30_000)));
        Assert.Empty(cw.Entries);

        Assert.True(cw.Report(E(m, 1_200_000)));
        Assert.Equal(0.2, cw.Entries[0].Fraction, 3);

        Assert.True(cw.Report(E(m, 5_700_000))); // 95 %
        Assert.Empty(cw.Entries);
    }

    [Fact]
    public void Latest_goes_first_with_one_entry_per_title()
    {
        var cw = new ContinueWatching();
        cw.Report(E(Movie("tt1"), 100_000, minutes: 1));
        cw.Report(E(Movie("tt2"), 100_000, minutes: 2));
        cw.Report(E(Movie("tt1"), 200_000, minutes: 3));
        Assert.Equal(new[] { "tt1", "tt2" }, cw.Entries.Select(e => e.Item.Id));
        Assert.Equal(200_000, cw.Entries[0].PositionMs);
    }

    [Fact]
    public void Matches_by_imdb_or_by_tmdb_id()
    {
        var cw = new ContinueWatching();
        cw.Report(E(Movie("tt0063350", tmdb: 10331), 600_000));
        Assert.NotNull(cw.Find(Movie("tmdb:10331", tmdb: 10331)));
        Assert.NotNull(cw.Find(Movie("tt0063350")));
        Assert.Null(cw.Find(Movie("tt9999999")));
    }

    [Fact]
    public void Series_keep_the_latest_episode_and_resume_only_that_one()
    {
        var show = new MediaItem { Id = "tt0903747", Type = MediaType.Series, Title = "Show" };
        var cw = new ContinueWatching();
        cw.Report(E(show, 300_000, season: 1, episode: 1));
        cw.Report(E(show, 400_000, season: 1, episode: 2, minutes: 1));
        Assert.Single(cw.Entries);
        Assert.Null(cw.Find(show, 1, 1));
        Assert.Equal(400_000, cw.Find(show, 1, 2)!.PositionMs);
    }

    [Fact]
    public void Keeps_at_most_twenty_titles()
    {
        var cw = new ContinueWatching();
        for (var i = 0; i < 25; i++) cw.Report(E(Movie($"tt{i}"), 100_000, minutes: i));
        Assert.Equal(ContinueWatching.MaxEntries, cw.Entries.Count);
        Assert.Equal("tt24", cw.Entries[0].Item.Id);
    }

    [Fact]
    public void Entries_with_source_survive_a_json_round_trip()
    {
        var source = new StreamCandidate
        {
            Addon = "addon.test", Quality = "1080p", Languages = new HashSet<string> { "en", "es" },
            InfoHash = "abcdef0123456789abcdef0123456789abcdef01", ConnectionHints = new[] { "udp://t:1/announce" },
            Headers = new Dictionary<string, string> { ["Referer"] = "x" }, Popularity = 100, Provider = "SiteC",
        };
        var list = new List<WatchEntry> { E(Movie("tt1"), 600_000) with { Source = source } };

        var back = new ContinueWatching(JsonSerializer.Deserialize<List<WatchEntry>>(JsonSerializer.Serialize(list)));

        var s = back.Entries.Single().Source!;
        Assert.True(s.NeedsResolver);
        Assert.Equal(source.InfoHash, s.InfoHash);
        Assert.Contains("es", s.Languages);
        Assert.Equal("x", s.Headers["Referer"]);
        Assert.Equal("SiteC", s.Provider);
    }
}
