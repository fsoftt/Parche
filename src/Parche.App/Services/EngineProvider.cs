using Parche.App.Plugins;
using Parche.Core;
using Parche.Core.Addons;
using Parche.Core.Models;

namespace Parche.App.Services;

/// <summary>Entrega el <see cref="ParcheEngine"/> vigente (se reconstruye al cambiar los ajustes) y cachea la portada 10 minutos.</summary>
public sealed class EngineProvider
{
    private static readonly TimeSpan HomeTtl = TimeSpan.FromMinutes(10);

    private readonly SettingsService _settings;
    private readonly HttpClient _http;
    private readonly IReadOnlyList<IStreamSourceProvider> _providers;
    private readonly StreamResolvers _resolvers;
    private readonly object _gate = new();
    private (ParcheEngine Engine, DateTime At, HomeFeed Feed)? _home;

    public EngineProvider(SettingsService settings, HttpClient http, IEnumerable<IStreamSourceProvider> providers, StreamResolvers resolvers)
    {
        _settings = settings;
        _http = http;
        _providers = providers.ToList();
        _resolvers = resolvers;
        Engine = Create(settings.Current);
        settings.Changed += (_, _) => Rebuild();
    }

    public ParcheEngine Engine { get; private set; }

    /// <summary>Motor con los proveedores de fuentes de los plugins; descarta las fuentes que la app no sabe reproducir (sin resolvedor).</summary>
    private ParcheEngine Create(Parche.Core.Settings.AppSettings s) => new(s, _http, providers: _providers, candidateFilter: _resolvers.CanPlay);

    /// <summary>El motor se reconstruyó con nuevos ajustes: las pantallas deben recargar sus datos.</summary>
    public event EventHandler? Changed;

    private void Rebuild()
    {
        lock (_gate)
        {
            Engine = Create(_settings.Current);
            _home = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<HomeFeed> GetHomeAsync(bool refresh, CancellationToken ct)
    {
        var engine = Engine;
        if (!refresh && _home is { } h && ReferenceEquals(h.Engine, engine) && DateTime.UtcNow - h.At < HomeTtl)
            return h.Feed;

        var feed = await engine.GetHomeAsync(ct);
        lock (_gate)
        {
            if (ReferenceEquals(Engine, engine)) _home = (engine, DateTime.UtcNow, feed);
        }
        return feed;
    }
}
