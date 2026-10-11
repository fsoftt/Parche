using Parche.Core.Models;

namespace Parche.Core.Catalog;

/// <summary>Catálogo de títulos basado en ids de IMDb (TMDB o Cinemeta).</summary>
public interface ICatalogProvider
{
    string Name { get; }

    Task<HomeFeed> GetHomeAsync(string lang, CancellationToken ct = default);

    Task<IReadOnlyList<MediaItem>> SearchAsync(string query, string lang, CancellationToken ct = default);

    /// <summary>Detalle de un título del listado (resuelve el id de IMDb si el listado no lo traía).</summary>
    Task<MediaDetail> GetDetailAsync(MediaItem item, string lang, CancellationToken ct = default);

    /// <summary>Detalle por tipo + id (IMDb "tt…" o, en TMDB, el id numérico).</summary>
    Task<MediaDetail> GetDetailAsync(MediaType type, string id, string lang, CancellationToken ct = default);

    /// <summary>Episodios de una temporada (TMDB los pide aquí; Cinemeta los devuelve de la ficha cacheada).</summary>
    Task<Season> GetSeasonAsync(MediaDetail show, int seasonNumber, string lang, CancellationToken ct = default);

    Task<IReadOnlyList<Genre>> GetGenresAsync(MediaType type, string lang, CancellationToken ct = default);

    /// <summary>Página <paramref name="page"/> (desde 1) de un género.</summary>
    Task<IReadOnlyList<MediaItem>> GetByGenreAsync(MediaType type, Genre genre, int page, string lang, CancellationToken ct = default);
}
