namespace Parche.Core.Models;

public enum MediaType
{
    Movie,
    Series,
}

public static class MediaTypeExtensions
{
    /// <summary>Nombre del tipo en el protocolo Stremio ("movie" | "series").</summary>
    public static string ToStremio(this MediaType type) => type == MediaType.Series ? "series" : "movie";

    public static MediaType FromStremio(string? type) =>
        string.Equals(type, "series", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "tv", StringComparison.OrdinalIgnoreCase)
            ? MediaType.Series : MediaType.Movie;
}

/// <summary>Tarjeta de contenido en un listado.</summary>
public sealed record MediaItem
{
    /// <summary>Id de IMDb ("tt…") cuando se conoce; si no, vacío y se resuelve vía <see cref="TmdbId"/>.</summary>
    public string Id { get; init; } = "";
    public int? TmdbId { get; init; }
    public MediaType Type { get; init; }
    public required string Title { get; init; }
    public int? Year { get; init; }
    public string? Poster { get; init; }
    public string? Backdrop { get; init; }
    public string? Description { get; init; }
    /// <summary>Puntuación 0–10.</summary>
    public double? Rating { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = Array.Empty<string>();

    /// <summary>Imagen horizontal preferida (banner); cae al póster si no hay.</summary>
    public string? Wide => Backdrop ?? Poster;

    public bool HasImdbId => Id.StartsWith("tt", StringComparison.Ordinal);
}

/// <summary>Fila horizontal de la portada.</summary>
public sealed record MediaSection(string Title, IReadOnlyList<MediaItem> Items);

public sealed record HomeFeed(MediaItem? Hero, IReadOnlyList<MediaSection> Sections);

public sealed record Genre(string Id, string Name);

public sealed record Episode
{
    public int Season { get; init; }
    public int Number { get; init; }
    public required string Title { get; init; }
    public string? Overview { get; init; }
    public string? Thumbnail { get; init; }
    public DateTime? Released { get; init; }
}

public sealed record Season(int Number, string Title, IReadOnlyList<Episode> Episodes);

public sealed record MediaDetail
{
    public string Id { get; init; } = "";
    public int? TmdbId { get; init; }
    public MediaType Type { get; init; }
    public required string Title { get; init; }
    public int? Year { get; init; }
    public string? Poster { get; init; }
    public string? Backdrop { get; init; }
    public string? Description { get; init; }
    public double? Rating { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = Array.Empty<string>();
    public int? Runtime { get; init; }
    /// <summary>Primeros 8 nombres del reparto.</summary>
    public IReadOnlyList<string> Cast { get; init; } = Array.Empty<string>();
    /// <summary>Temporadas (con episodios cargados en Cinemeta; en TMDB se cargan con GetSeasonAsync).</summary>
    public IReadOnlyList<Season> Seasons { get; init; } = Array.Empty<Season>();
    /// <summary>URL del tráiler (YouTube) si existe.</summary>
    public string? Trailer { get; init; }

    public bool IsSeries => Type == MediaType.Series;
    public bool HasImdbId => Id.StartsWith("tt", StringComparison.Ordinal);

    public MediaItem ToItem() => new()
    {
        Id = Id, TmdbId = TmdbId, Type = Type, Title = Title, Year = Year, Poster = Poster,
        Backdrop = Backdrop, Description = Description, Rating = Rating, Genres = Genres,
    };
}

public enum StreamKind
{
    Progressive,
    Hls,
    Dash,
}

public sealed record SubtitleTrack(string Url, string? Language, string Label, string MimeType);

/// <summary>Lo que necesita el reproductor para abrir un contenido.</summary>
public sealed record PlaybackRequest
{
    public required string Url { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public StreamKind Kind { get; init; }
    public IReadOnlyList<SubtitleTrack> Subtitles { get; init; } = Array.Empty<SubtitleTrack>();
    public string? Title { get; init; }
    /// <summary>Posición inicial en milisegundos (para continuar donde se dejó); 0 = desde el principio.</summary>
    public long StartPositionMs { get; init; }

    public static StreamKind Guess(string url)
    {
        var path = url.Split('?', '#')[0].ToLowerInvariant();
        if (path.EndsWith(".m3u8")) return StreamKind.Hls;
        if (path.EndsWith(".mpd")) return StreamKind.Dash;
        return StreamKind.Progressive;
    }
}
