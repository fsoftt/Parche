using Parche.Core.Addons;
using Parche.Core.Library;
using Parche.Core.Models;
using Parche.Core.Settings;

namespace Parche.Core.Tests;

public class DownloadQueueTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static MediaItem Movie(string id, int? tmdb = null) => new() { Id = id, TmdbId = tmdb, Type = MediaType.Movie, Title = id };

    private static StreamCandidate Hashed(string hash) => new() { InfoHash = hash, Title = hash, Quality = "1080p" };

    private static DownloadItem D(MediaItem item, int? season = null, int? episode = null, params StreamCandidate[] alternatives) => new()
    {
        Id = DownloadQueue.KeyFor(item, season, episode), Item = item, Season = season, Episode = episode,
        Source = Hashed("aaaa"), Alternatives = alternatives,
    };

    private static DateTime At(int minutes) => T0.AddMinutes(minutes);

    [Fact]
    public void One_download_per_title_and_episode()
    {
        var q = new DownloadQueue();
        Assert.True(q.Enqueue(D(Movie("tt1")), At(0)));
        Assert.False(q.Enqueue(D(Movie("tt1")), At(1)));
        Assert.True(q.Enqueue(D(Movie("tt2"), 1, 1), At(2)));
        Assert.True(q.Enqueue(D(Movie("tt2"), 1, 2), At(3)));
        Assert.False(q.Enqueue(D(Movie("tt2"), 1, 2), At(4)));
        Assert.Equal(3, q.Items.Count);
        Assert.Equal("tt2:1:2", q.Items[2].Id);
    }

    [Fact]
    public void Key_uses_tmdb_when_there_is_no_imdb_id()
    {
        var a = DownloadQueue.KeyFor(Movie("", 603));
        Assert.Equal("tmdb-movie-603", a);
        Assert.Equal("tt9", DownloadQueue.KeyFor(Movie("tt9", 603)));
        Assert.Equal("tt9:2:5", DownloadQueue.KeyFor(Movie("tt9"), 2, 5));
        Assert.Equal("tt9_2_5", DownloadQueue.SafeFolder("tt9:2:5"));
    }

    [Fact]
    public void Keeps_arrival_order_and_respects_concurrency()
    {
        var q = new DownloadQueue();
        q.Enqueue(D(Movie("tt1")), At(0));
        q.Enqueue(D(Movie("tt2")), At(1));
        q.Enqueue(D(Movie("tt3")), At(2));

        Assert.Equal(["tt1"], q.NextToStart().Select(i => i.Id));
        Assert.True(q.Start("tt1", At(3)));
        Assert.Empty(q.NextToStart()); // ya hay una en curso y el máximo es 1

        Assert.True(q.Complete("tt1", "/f/video.mkv", At(4)));
        Assert.Equal(["tt2"], q.NextToStart().Select(i => i.Id));

        var two = new DownloadQueue(maxConcurrent: 2);
        two.Enqueue(D(Movie("tt1")), At(0));
        two.Enqueue(D(Movie("tt2")), At(1));
        two.Enqueue(D(Movie("tt3")), At(2));
        Assert.Equal(["tt1", "tt2"], two.NextToStart().Select(i => i.Id));
        two.Start("tt1", At(3));
        Assert.Equal(["tt2"], two.NextToStart().Select(i => i.Id));
    }

    [Fact]
    public void State_transitions()
    {
        var q = new DownloadQueue();
        q.Enqueue(D(Movie("tt1")), At(0));

        Assert.False(q.Resume("tt1", At(1))); // en cola: nada que reanudar
        Assert.True(q.Start("tt1", At(1)));
        Assert.False(q.Start("tt1", At(2)));
        Assert.True(q.Pause("tt1", At(2)));
        Assert.Equal(DownloadState.Paused, q.Find("tt1")!.State);
        Assert.Empty(q.NextToStart());

        Assert.True(q.Resume("tt1", At(3)));
        Assert.Equal(DownloadState.Queued, q.Find("tt1")!.State);
        q.Start("tt1", At(4));
        Assert.True(q.Requeue("tt1", At(5)));
        Assert.Equal(DownloadState.Queued, q.Find("tt1")!.State);

        q.Start("tt1", At(6));
        Assert.True(q.Fail("tt1", "sin espacio", At(7)));
        var failed = q.Find("tt1")!;
        Assert.Equal(DownloadState.Failed, failed.State);
        Assert.Equal("sin espacio", failed.Error);
        Assert.False(q.Pause("tt1", At(8)));

        Assert.True(q.Resume("tt1", At(9)));
        Assert.Null(q.Find("tt1")!.Error);
        q.Start("tt1", At(10));
        Assert.True(q.Complete("tt1", "/f/video.mkv", At(11)));
        Assert.False(q.Pause("tt1", At(12)));
        Assert.False(q.Resume("tt1", At(12)));
        Assert.Equal(1, q.Find("tt1")!.Fraction);
        Assert.Equal("/f/video.mkv", q.Find("tt1")!.FilePath);
    }

    [Fact]
    public void Progress_only_counts_while_downloading_and_is_clamped()
    {
        var q = new DownloadQueue();
        q.Enqueue(D(Movie("tt1")), At(0));
        Assert.False(q.Report("tt1", 10, 100)); // aún en cola

        q.Start("tt1", At(1));
        Assert.True(q.Report("tt1", 25, 100));
        Assert.Equal(0.25, q.Find("tt1")!.Fraction, 3);
        Assert.False(q.Report("tt1", 25, 100)); // sin cambios
        Assert.True(q.Report("tt1", 500, 0));    // sin total nuevo conserva el anterior y limita
        Assert.Equal(100, q.Find("tt1")!.BytesDone);
        Assert.False(q.Report("nope", 1, 1));
    }

    [Fact]
    public void Failed_download_can_be_enqueued_again_with_a_new_source()
    {
        var q = new DownloadQueue();
        q.Enqueue(D(Movie("tt1")), At(0));
        q.Start("tt1", At(1));
        q.Report("tt1", 50, 100);
        q.Fail("tt1", "x", At(2));

        var again = D(Movie("tt1")) with { Source = Hashed("bbbb") };
        Assert.True(q.Enqueue(again, At(3)));
        var item = q.Find("tt1")!;
        Assert.Equal(DownloadState.Queued, item.State);
        Assert.Equal("bbbb", item.Source.InfoHash);
        Assert.Equal(0, item.BytesDone);
        Assert.Null(item.Error);
        Assert.Single(q.Items);
    }

    [Fact]
    public void Switches_to_the_next_source_until_none_are_left()
    {
        var q = new DownloadQueue();
        q.Enqueue(D(Movie("tt1"), null, null, Hashed("bbbb"), Hashed("cccc")), At(0));
        q.Start("tt1", At(1));
        q.Report("tt1", 10, 100);

        Assert.Equal("bbbb", q.NextSource("tt1", At(2))!.InfoHash);
        Assert.Equal("bbbb", q.Find("tt1")!.Source.InfoHash);
        Assert.Equal(0, q.Find("tt1")!.BytesDone);
        Assert.Equal("cccc", q.NextSource("tt1", At(3))!.InfoHash);
        Assert.Null(q.NextSource("tt1", At(4)));
        Assert.Null(q.NextSource("nope", At(4)));
    }

    [Fact]
    public void Remove_deletes_the_entry()
    {
        var q = new DownloadQueue();
        q.Enqueue(D(Movie("tt1")), At(0));
        Assert.True(q.Remove("tt1"));
        Assert.False(q.Remove("tt1"));
        Assert.Empty(q.Items);
        Assert.True(q.Enqueue(D(Movie("tt1")), At(1))); // se puede volver a pedir
    }

    [Fact]
    public void Json_round_trip_keeps_everything_and_requeues_interrupted_downloads()
    {
        var q = new DownloadQueue();
        var sub = new SubtitleTrack("https://s/1.srt", "es", "Español", "application/x-subrip");
        var src = new StreamCandidate
        {
            InfoHash = "aaaa", Title = "t", Quality = "720p", SizeBytes = 123, Languages = new HashSet<string> { "es", "en" },
            Headers = new Dictionary<string, string> { ["X"] = "y" }, ConnectionHints = ["udp://t:1"],
        };
        q.Enqueue(D(Movie("tt1")) with { Source = src, Subtitles = [sub], Alternatives = [Hashed("bbbb")] }, At(0));
        q.Enqueue(D(Movie("tt2"), 1, 3) with { EpisodeTitle = "Piloto" }, At(1));
        q.Enqueue(D(Movie("tt3")), At(2));
        q.Start("tt1", At(3));
        q.Report("tt1", 40, 100);
        q.Start("tt1", At(3)); // no-op
        q.Complete("tt3", "/f/video.mp4", At(4));
        q.SetLocalSubtitles("tt3", [sub with { Url = "file:///f/sub_0.srt" }]);

        var copy = DownloadQueue.FromJson(q.ToJson());

        Assert.Equal(["tt1", "tt2:1:3", "tt3"], copy.Items.Select(i => i.Id));
        var a = copy.Find("tt1")!;
        Assert.Equal(DownloadState.Queued, a.State); // estaba en curso al guardar
        Assert.Equal(40, a.BytesDone);
        Assert.Equal(100, a.BytesTotal);
        Assert.Equal(new HashSet<string> { "es", "en" }, a.Source.Languages.ToHashSet());
        Assert.Equal("y", a.Source.Headers["X"]);
        Assert.Equal("udp://t:1", Assert.Single(a.Source.ConnectionHints));
        Assert.Equal(sub, Assert.Single(a.Subtitles));
        Assert.Equal("bbbb", Assert.Single(a.Alternatives).InfoHash);
        Assert.Equal("Piloto", copy.Find("tt2:1:3")!.EpisodeTitle);
        var c = copy.Find("tt3")!;
        Assert.Equal(DownloadState.Completed, c.State);
        Assert.Equal("/f/video.mp4", c.FilePath);
        Assert.Equal("file:///f/sub_0.srt", Assert.Single(c.LocalSubtitles).Url);
        Assert.Contains("\"Completed\"", q.ToJson()); // los estados van como texto
    }

    [Fact]
    public void Corrupt_json_gives_an_empty_queue()
    {
        Assert.Empty(DownloadQueue.FromJson("{not json").Items);
        Assert.Empty(DownloadQueue.FromJson("").Items);
        Assert.Empty(DownloadQueue.FromJson(null).Items);
    }

    [Fact]
    public void Wifi_only_is_on_by_default_and_survives_json()
    {
        Assert.True(new AppSettings().DownloadOnlyOnWifi);
        Assert.True(AppSettings.FromJson("{}").DownloadOnlyOnWifi);
        var off = new AppSettings { DownloadOnlyOnWifi = false };
        Assert.False(AppSettings.FromJson(off.ToJson()).DownloadOnlyOnWifi);
    }
}
