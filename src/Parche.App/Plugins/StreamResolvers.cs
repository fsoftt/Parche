using Parche.Core.Addons;

namespace Parche.App.Plugins;

/// <summary>
/// Reúne los <see cref="IStreamResolver"/> registrados. Sin ninguno, solo se pueden reproducir los candidatos con enlace directo.
/// </summary>
public sealed class StreamResolvers(IEnumerable<IStreamResolver> resolvers)
{
    private readonly IReadOnlyList<IStreamResolver> _resolvers = resolvers.ToList();

    /// <summary>Hay al menos un resolvedor registrado.</summary>
    public bool Any => _resolvers.Count > 0;

    private IStreamResolver? Find(StreamCandidate c) => _resolvers.FirstOrDefault(r => r.CanResolve(c));

    /// <summary>Se puede reproducir: tiene enlace directo o algún resolvedor lo soporta.</summary>
    public bool CanPlay(StreamCandidate c) => !string.IsNullOrEmpty(c.Url) || (c.NeedsResolver && Find(c) is not null);

    /// <summary>El candidato necesita un resolvedor y hay uno que lo soporta.</summary>
    public bool CanResolve(StreamCandidate c) => c.NeedsResolver && Find(c) is not null;

    public Task<IResolvedStream> ResolveAsync(StreamCandidate c, IProgress<ResolveStatus>? progress, CancellationToken ct) =>
        (Find(c) ?? throw new StreamResolveException("No hay un resolvedor para esta fuente.")).ResolveAsync(c, progress, ct);

    public Task PreloadAsync(StreamCandidate c) => Find(c)?.PreloadAsync(c) ?? Task.CompletedTask;

    public Task PreloadAlternateAsync(StreamCandidate c) => Find(c)?.PreloadAlternateAsync(c) ?? Task.CompletedTask;

    public Task CancelPreloadAsync() => Task.WhenAll(_resolvers.Select(r => r.CancelPreloadAsync()));

    public Task StopAsync() => Task.WhenAll(_resolvers.Select(r => r.StopAsync()));
}

/// <summary>Reúne los <see cref="IDownloadResolver"/> registrados.</summary>
public sealed class DownloadResolvers(IEnumerable<IDownloadResolver> resolvers)
{
    private readonly IReadOnlyList<IDownloadResolver> _resolvers = resolvers.ToList();

    public IDownloadResolver? Find(StreamCandidate c) => _resolvers.FirstOrDefault(r => r.CanDownload(c));

    public bool CanDownload(StreamCandidate c) => Find(c) is not null;
}
