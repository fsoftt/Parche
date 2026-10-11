using System.Net;
using Parche.Core.Models;

namespace Parche.Core.Catalog;

/// <summary>
/// Usa TMDB si hay clave configurada y Cinemeta si no. Si TMDB responde 401 (clave inválida)
/// cambia a Cinemeta de forma permanente durante la vida de la instancia.
/// </summary>
public sealed class CompositeCatalog : ICatalogProvider
{
    private readonly ICatalogProvider? _primary;
    private readonly ICatalogProvider _fallback;
    private volatile bool _primaryRejected;

    public CompositeCatalog(ICatalogProvider? primary, ICatalogProvider fallback)
    {
        _primary = primary;
        _fallback = fallback;
    }

    /// <summary>true si TMDB fue rechazado (401) y se está usando Cinemeta.</summary>
    public bool PrimaryRejected => _primaryRejected;

    public string Name => Active.Name;

    private ICatalogProvider Active => _primary is null || _primaryRejected ? _fallback : _primary;

    private async Task<T> RunAsync<T>(Func<ICatalogProvider, Task<T>> call)
    {
        if (_primary is null || _primaryRejected) return await call(_fallback);
        try
        {
            return await call(_primary);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            _primaryRejected = true;
            return await call(_fallback);
        }
    }

    public Task<HomeFeed> GetHomeAsync(string lang, CancellationToken ct = default) => RunAsync(p => p.GetHomeAsync(lang, ct));
    public Task<IReadOnlyList<MediaItem>> SearchAsync(string query, string lang, CancellationToken ct = default) => RunAsync(p => p.SearchAsync(query, lang, ct));
    public Task<MediaDetail> GetDetailAsync(MediaItem item, string lang, CancellationToken ct = default) => RunAsync(p => p.GetDetailAsync(item, lang, ct));
    public Task<MediaDetail> GetDetailAsync(MediaType type, string id, string lang, CancellationToken ct = default) => RunAsync(p => p.GetDetailAsync(type, id, lang, ct));
    public Task<Season> GetSeasonAsync(MediaDetail show, int seasonNumber, string lang, CancellationToken ct = default) => RunAsync(p => p.GetSeasonAsync(show, seasonNumber, lang, ct));
    public Task<IReadOnlyList<Genre>> GetGenresAsync(MediaType type, string lang, CancellationToken ct = default) => RunAsync(p => p.GetGenresAsync(type, lang, ct));
    public Task<IReadOnlyList<MediaItem>> GetByGenreAsync(MediaType type, Genre genre, int page, string lang, CancellationToken ct = default) => RunAsync(p => p.GetByGenreAsync(type, genre, page, lang, ct));
}
