using Parche.Core.Addons;
using Parche.Core.Models;

namespace Parche.Core.Library;

/// <summary>Un título a medio ver: hasta dónde llegó y con qué fuente, para retomarlo.</summary>
public sealed record WatchEntry
{
    public required MediaItem Item { get; init; }
    /// <summary>Episodio en curso (series); null en películas.</summary>
    public int? Season { get; init; }
    public int? Episode { get; init; }
    public string? EpisodeTitle { get; init; }
    public long PositionMs { get; init; }
    public long DurationMs { get; init; }
    public DateTime UpdatedUtc { get; init; }
    /// <summary>Fuente con la que se estaba viendo; permite retomar sin volver a elegirla.</summary>
    public StreamCandidate? Source { get; init; }

    /// <summary>Fracción vista (0..1); 0 si no se conoce la duración.</summary>
    public double Fraction => DurationMs > 0 ? Math.Clamp((double)PositionMs / DurationMs, 0, 1) : 0;

    public bool IsSameEpisode(int? season, int? episode) => Season == season && Episode == episode;
}

/// <summary>
/// Lista de "Continuar viendo": una entrada por título (en series, el último episodio), de la más reciente a
/// la más antigua. Ignora lo visto menos de un minuto y quita lo que se terminó de ver.
/// </summary>
public sealed class ContinueWatching
{
    public const long MinPositionMs = 60_000;
    public const double FinishedFraction = 0.92;
    public const int MaxEntries = 20;

    private readonly List<WatchEntry> _entries;

    public ContinueWatching(IEnumerable<WatchEntry>? entries = null)
    {
        _entries = (entries ?? Array.Empty<WatchEntry>())
            .Where(e => IsIdentifiable(e.Item))
            .OrderByDescending(e => e.UpdatedUtc)
            .ToList();
        // Una entrada por título: se queda la más reciente.
        for (var i = _entries.Count - 1; i > 0; i--)
            if (_entries.Take(i).Any(e => Same(e.Item, _entries[i].Item))) _entries.RemoveAt(i);
        if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
    }

    public IReadOnlyList<WatchEntry> Entries => _entries;

    /// <summary>Registra el avance. Devuelve true si la lista cambió.</summary>
    public bool Report(WatchEntry entry)
    {
        if (!IsIdentifiable(entry.Item)) return false;
        var index = _entries.FindIndex(e => Same(e.Item, entry.Item));

        var finished = entry.DurationMs > 0 && entry.PositionMs >= entry.DurationMs * FinishedFraction;
        if (finished)
        {
            if (index < 0) return false;
            _entries.RemoveAt(index);
            return true;
        }
        if (entry.PositionMs < MinPositionMs) return false;

        if (index >= 0) _entries.RemoveAt(index);
        _entries.Insert(0, entry);
        if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        return true;
    }

    /// <summary>Posición guardada para ese título y episodio, o null si no hay.</summary>
    public WatchEntry? Find(MediaItem item, int? season = null, int? episode = null) =>
        _entries.FirstOrDefault(e => Same(e.Item, item) && e.IsSameEpisode(season, episode));

    public bool Remove(MediaItem item) => _entries.RemoveAll(e => Same(e.Item, item)) > 0;

    private static bool IsIdentifiable(MediaItem i) => i.HasImdbId || i.TmdbId is not null;

    /// <summary>Mismo título: igual id de IMDb o mismo tipo e id de TMDB (un listado de TMDB puede no traer el de IMDb).</summary>
    private static bool Same(MediaItem a, MediaItem b) =>
        (a.HasImdbId && b.HasImdbId && a.Id == b.Id)
        || (a.TmdbId is { } ta && b.TmdbId is { } tb && ta == tb && a.Type == b.Type);
}
