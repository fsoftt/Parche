using System.Text.Json;
using System.Text.Json.Serialization;
using Parche.Core.Addons;
using Parche.Core.Models;

namespace Parche.Core.Library;

public enum DownloadState
{
    /// <summary>Esperando su turno (o a que haya una red permitida).</summary>
    Queued,
    Downloading,
    /// <summary>Detenida por el usuario; conserva lo descargado.</summary>
    Paused,
    Completed,
    Failed,
}

/// <summary>Una descarga para ver sin conexión: qué título, con qué fuente y cuánto lleva.</summary>
public sealed record DownloadItem
{
    /// <summary>Clave única del título y episodio (<see cref="DownloadQueue.KeyFor"/>); también nombra su carpeta.</summary>
    public required string Id { get; init; }
    public required MediaItem Item { get; init; }
    /// <summary>Episodio (series); null en películas.</summary>
    public int? Season { get; init; }
    public int? Episode { get; init; }
    public string? EpisodeTitle { get; init; }
    /// <summary>Fuente en uso; cambia a una de <see cref="Alternatives"/> si no responde.</summary>
    public required StreamCandidate Source { get; init; }
    /// <summary>Otras fuentes del mismo título, en orden, por si la elegida no responde.</summary>
    public IReadOnlyList<StreamCandidate> Alternatives { get; init; } = Array.Empty<StreamCandidate>();
    /// <summary>Subtítulos externos por descargar junto al video.</summary>
    public IReadOnlyList<SubtitleTrack> Subtitles { get; init; } = Array.Empty<SubtitleTrack>();
    /// <summary>Subtítulos ya guardados (URL de archivo local).</summary>
    public IReadOnlyList<SubtitleTrack> LocalSubtitles { get; init; } = Array.Empty<SubtitleTrack>();
    public DownloadState State { get; init; }
    public long BytesDone { get; init; }
    /// <summary>Tamaño total; 0 si aún no se conoce.</summary>
    public long BytesTotal { get; init; }
    /// <summary>Ruta del video terminado.</summary>
    public string? FilePath { get; init; }
    public string? Error { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime UpdatedUtc { get; init; }

    /// <summary>Fracción descargada (0..1); 0 si no se conoce el total.</summary>
    [JsonIgnore]
    public double Fraction => State == DownloadState.Completed ? 1 : BytesTotal > 0 ? Math.Clamp((double)BytesDone / BytesTotal, 0, 1) : 0;

    /// <summary>true si es un episodio de una serie.</summary>
    [JsonIgnore]
    public bool IsEpisode => Season is not null && Episode is not null;

    /// <summary>Nombre de carpeta seguro derivado del <see cref="Id"/>.</summary>
    [JsonIgnore]
    public string FolderName => DownloadQueue.SafeFolder(Id);
}

/// <summary>
/// Cola de descargas: una por título y episodio, en orden de llegada, con un máximo de descargas simultáneas.
/// Lógica pura (sin red ni disco): quien la use avisa de los cambios de estado y de progreso.
/// </summary>
public sealed class DownloadQueue
{
    public const int DefaultMaxConcurrent = 1;

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly List<DownloadItem> _items = new();

    /// <param name="items">Descargas guardadas. Las que estaban en curso (la app se cerró) vuelven a la cola.</param>
    /// <param name="maxConcurrent">Descargas simultáneas (mínimo 1).</param>
    public DownloadQueue(IEnumerable<DownloadItem>? items = null, int maxConcurrent = DefaultMaxConcurrent)
    {
        MaxConcurrent = Math.Max(1, maxConcurrent);
        foreach (var i in (items ?? Array.Empty<DownloadItem>()).OrderBy(i => i.CreatedUtc))
        {
            if (_items.Any(e => e.Id == i.Id)) continue;
            _items.Add(i.State == DownloadState.Downloading ? i with { State = DownloadState.Queued } : i);
        }
    }

    public int MaxConcurrent { get; }

    /// <summary>Todas las descargas, de la más antigua a la más reciente.</summary>
    public IReadOnlyList<DownloadItem> Items => _items;

    public int ActiveCount => _items.Count(i => i.State == DownloadState.Downloading);

    /// <summary>Clave del título: id de IMDb (o tipo e id de TMDB) y, en series, temporada y episodio.</summary>
    public static string KeyFor(MediaItem item, int? season = null, int? episode = null)
    {
        var title = item.HasImdbId ? item.Id : $"tmdb-{item.Type.ToStremio()}-{item.TmdbId}";
        return season is { } s && episode is { } e ? $"{title}:{s}:{e}" : title;
    }

    /// <summary>Convierte una clave en nombre de carpeta válido en cualquier sistema de archivos.</summary>
    public static string SafeFolder(string id)
    {
        var chars = id.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return chars.Length == 0 ? "_" : new string(chars);
    }

    public DownloadItem? Find(string id) => _items.FirstOrDefault(i => i.Id == id);

    public DownloadItem? Find(MediaItem item, int? season = null, int? episode = null) => Find(KeyFor(item, season, episode));

    /// <summary>
    /// Añade una descarga al final de la cola. Si ya existe una para ese título y episodio no se duplica: una
    /// fallida se vuelve a poner en cola con la fuente nueva; cualquier otra se deja como está.
    /// </summary>
    /// <returns>true si la lista cambió.</returns>
    public bool Enqueue(DownloadItem item, DateTime now)
    {
        var index = _items.FindIndex(i => i.Id == item.Id);
        if (index >= 0)
        {
            if (_items[index].State != DownloadState.Failed) return false;
            _items.RemoveAt(index);
        }
        _items.Add(item with
        {
            State = DownloadState.Queued, Error = null, BytesDone = 0, BytesTotal = 0, FilePath = null,
            CreatedUtc = now, UpdatedUtc = now,
        });
        return true;
    }

    /// <summary>Las descargas en cola que pueden empezar ahora, en orden, sin pasar de <see cref="MaxConcurrent"/>.</summary>
    public IReadOnlyList<DownloadItem> NextToStart() =>
        _items.Where(i => i.State == DownloadState.Queued).Take(Math.Max(0, MaxConcurrent - ActiveCount)).ToList();

    /// <summary>En cola → descargando.</summary>
    public bool Start(string id, DateTime now) =>
        Transition(id, now, [DownloadState.Queued], i => i with { State = DownloadState.Downloading, Error = null });

    /// <summary>Actualiza el avance de una descarga en curso (no toca <c>UpdatedUtc</c>: es muy frecuente).</summary>
    public bool Report(string id, long done, long total)
    {
        var index = _items.FindIndex(i => i.Id == id);
        if (index < 0 || _items[index].State != DownloadState.Downloading) return false;
        var cur = _items[index];
        var newTotal = total > 0 ? total : cur.BytesTotal;
        var newDone = Math.Max(0, newTotal > 0 ? Math.Min(done, newTotal) : done);
        if (newDone == cur.BytesDone && newTotal == cur.BytesTotal) return false;
        _items[index] = cur with { BytesDone = newDone, BytesTotal = newTotal };
        return true;
    }

    public bool Complete(string id, string filePath, DateTime now) => Transition(id, now,
        [DownloadState.Downloading, DownloadState.Queued, DownloadState.Paused],
        i =>
        {
            var size = Math.Max(i.BytesTotal, i.BytesDone);
            return i with { State = DownloadState.Completed, FilePath = filePath, Error = null, BytesDone = size, BytesTotal = size };
        });

    public bool Fail(string id, string error, DateTime now) => Transition(id, now,
        [DownloadState.Downloading, DownloadState.Queued], i => i with { State = DownloadState.Failed, Error = error });

    /// <summary>Pausa una descarga en cola o en curso.</summary>
    public bool Pause(string id, DateTime now) => Transition(id, now,
        [DownloadState.Queued, DownloadState.Downloading], i => i with { State = DownloadState.Paused });

    /// <summary>Reanuda una pausada o fallida: vuelve a la cola.</summary>
    public bool Resume(string id, DateTime now) => Transition(id, now,
        [DownloadState.Paused, DownloadState.Failed], i => i with { State = DownloadState.Queued, Error = null });

    /// <summary>Devuelve a la cola una descarga en curso (por ejemplo, al perder el WiFi).</summary>
    public bool Requeue(string id, DateTime now) => Transition(id, now,
        [DownloadState.Downloading], i => i with { State = DownloadState.Queued });

    /// <summary>Pasa a la siguiente fuente de las alternativas (sin avance). Devuelve la nueva fuente, o null si no quedan.</summary>
    public StreamCandidate? NextSource(string id, DateTime now)
    {
        var index = _items.FindIndex(i => i.Id == id);
        if (index < 0 || _items[index].Alternatives.Count == 0) return null;
        var cur = _items[index];
        var next = cur.Alternatives[0];
        _items[index] = cur with
        {
            Source = next, Alternatives = cur.Alternatives.Skip(1).ToList(), BytesDone = 0, BytesTotal = 0, UpdatedUtc = now,
        };
        return next;
    }

    /// <summary>Guarda los subtítulos descargados (rutas locales).</summary>
    public bool SetLocalSubtitles(string id, IReadOnlyList<SubtitleTrack> tracks)
    {
        var index = _items.FindIndex(i => i.Id == id);
        if (index < 0) return false;
        _items[index] = _items[index] with { LocalSubtitles = tracks };
        return true;
    }

    public bool Remove(string id) => _items.RemoveAll(i => i.Id == id) > 0;

    private bool Transition(string id, DateTime now, DownloadState[] from, Func<DownloadItem, DownloadItem> change)
    {
        var index = _items.FindIndex(i => i.Id == id);
        if (index < 0 || !from.Contains(_items[index].State)) return false;
        _items[index] = change(_items[index]) with { UpdatedUtc = now };
        return true;
    }

    public string ToJson() => JsonSerializer.Serialize(_items, Json);

    /// <summary>Lee una cola guardada; un JSON ilegible da una cola vacía.</summary>
    public static DownloadQueue FromJson(string? json, int maxConcurrent = DefaultMaxConcurrent)
    {
        List<DownloadItem>? items = null;
        try { if (!string.IsNullOrWhiteSpace(json)) items = JsonSerializer.Deserialize<List<DownloadItem>>(json, Json); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
        return new DownloadQueue(items, maxConcurrent);
    }
}
