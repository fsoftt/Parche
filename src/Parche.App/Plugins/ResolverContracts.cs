using Parche.Core.Addons;

namespace Parche.App.Plugins;

/// <summary>Fase del arranque de una fuente que un resolvedor prepara.</summary>
public enum ResolveStage
{
    /// <summary>Iniciando.</summary>
    Starting,
    /// <summary>Conectando con la fuente y obteniendo los datos del video.</summary>
    Connecting,
    /// <summary>Cargando el inicio del video en el búfer.</summary>
    Buffering,
    /// <summary>El video ya se puede reproducir desde la URL local.</summary>
    Ready,
    /// <summary>Ocurrió un error.</summary>
    Error,
    /// <summary>Detenido.</summary>
    Stopped,
}

/// <summary>Estado instantáneo de una fuente en preparación o en reproducción.</summary>
/// <param name="Stage">Fase actual.</param>
/// <param name="Progress">Avance de la carga, entre 0 y 1.</param>
/// <param name="BytesPerSecond">Velocidad de descarga.</param>
/// <param name="Connections">Conexiones abiertas con la fuente.</param>
/// <param name="Message">Mensaje opcional (p. ej. el error).</param>
public sealed record ResolveStatus(ResolveStage Stage, double Progress, long BytesPerSecond, int Connections, string? Message = null);

/// <summary>Error esperado al preparar o descargar una fuente (no responde, sin datos…): la app prueba la siguiente.</summary>
public sealed class StreamResolveException : Exception
{
    public StreamResolveException(string message) : base(message) { }
    public StreamResolveException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Una fuente ya preparada: expone una URL local que el reproductor puede abrir.</summary>
public interface IResolvedStream : IAsyncDisposable
{
    /// <summary>URL desde la que se reproduce.</summary>
    Uri Url { get; }
    string FileName { get; }
    long FileSize { get; }
    ResolveStatus Current { get; }
    /// <summary>Cambió el estado (puede venir de cualquier hilo).</summary>
    event EventHandler<ResolveStatus>? StatusChanged;
}

/// <summary>
/// Convierte un candidato que no se puede reproducir directamente (tiene identificador y no tiene enlace) en una URL
/// reproducible. Lo aporta un plugin; sin ninguno registrado, esos candidatos se descartan de la lista.
/// </summary>
public interface IStreamResolver
{
    /// <summary>Este resolvedor sabe preparar el candidato.</summary>
    bool CanResolve(StreamCandidate candidate);

    /// <summary>Prepara el candidato y cierra antes la sesión anterior; solo hay una activa. Lanza <see cref="StreamResolveException"/> si la fuente no responde.</summary>
    Task<IResolvedStream> ResolveAsync(StreamCandidate candidate, IProgress<ResolveStatus>? progress, CancellationToken ct);

    /// <summary>Empieza a preparar el candidato en segundo plano (mientras el usuario mira la lista) para que arranque antes. No hace nada si ya hay una sesión activa.</summary>
    Task PreloadAsync(StreamCandidate candidate);

    /// <summary>Como <see cref="PreloadAsync"/>, pero válido mientras otra fuente se está preparando (respaldo).</summary>
    Task PreloadAlternateAsync(StreamCandidate candidate);

    /// <summary>Descarta la precarga pendiente.</summary>
    Task CancelPreloadAsync();

    /// <summary>Cierra la sesión activa y libera sus recursos.</summary>
    Task StopAsync();
}

/// <summary>Avance de una descarga para ver sin conexión.</summary>
public readonly record struct DownloadProgress(long BytesDone, long BytesTotal, long BytesPerSecond);

/// <summary>
/// Descarga completa de un candidato que no es un enlace directo. La descarga HTTP común la hace la app; sin un
/// resolvedor que lo soporte, la descarga no se ofrece para ese candidato.
/// </summary>
public interface IDownloadResolver
{
    bool CanDownload(StreamCandidate candidate);

    /// <summary>Descarga el video completo y devuelve la ruta del archivo en <paramref name="outputDirectory"/>.</summary>
    /// <param name="workDirectory">Datos parciales y estado de reanudación; se conservan hasta terminar.</param>
    Task<string> DownloadAsync(StreamCandidate candidate, string workDirectory, string outputDirectory,
        IProgress<DownloadProgress>? progress, CancellationToken ct);
}
