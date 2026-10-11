using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Parche.App.Localization;
using Parche.App.Plugins;
using Parche.Core.Addons;
using Parche.Core.Library;
using Parche.Core.Models;

namespace Parche.App.Services;

/// <summary>
/// Descargas para ver sin conexión. Ejecuta la cola (<see cref="DownloadQueue"/>) con HTTP y reanudación por Range, o con un
/// <see cref="IDownloadResolver"/> de un plugin para las fuentes que no son enlaces directos; la guarda en Preferences y mantiene un servicio
/// en primer plano de Android mientras algo se descarga (en Windows la app sigue abierta y no hace falta). Los archivos van a <c>AppDataDirectory/downloads</c>
/// (almacenamiento propio de la app: no pide permisos y se borra al desinstalar).
/// </summary>
public sealed class DownloadService
{
    private const string Key = "downloads_v1";
    private const int MaxAlternatives = 4;
    private const int MaxSubtitles = 4;
    private const long UnknownSizeReserve = 600L * 1024 * 1024;
    private static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RaiseEvery = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan NotifyEvery = TimeSpan.FromSeconds(2);

    private sealed class Run(CancellationTokenSource cts)
    {
        public CancellationTokenSource Cts { get; } = cts;
        public Task Task { get; set; } = Task.CompletedTask;
    }

    private sealed class SpaceException : Exception;

    private sealed class UnsupportedSourceException : Exception;

    private readonly SettingsService _settings;
    private readonly DownloadResolvers _resolvers;
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private readonly DownloadQueue _queue;
    private readonly Dictionary<string, Run> _runs = new();
    private readonly ConcurrentDictionary<string, long> _speeds = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastRaise = new();
    private DateTime _lastSave = DateTime.MinValue;
    private DateTime _lastNotify = DateTime.MinValue;
    private bool _dirty;
    private bool _started;
    private readonly object _saveGate = new();

    public DownloadService(SettingsService settings, DownloadResolvers resolvers)
    {
        _settings = settings;
        _resolvers = resolvers;
        // Sin tiempo límite global: una descarga larga no es un error. Los reintentos los decide el usuario.
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(HttpDefaults.UserAgent);

        string json;
        try { json = Preferences.Default.Get(Key, ""); }
        catch (Exception) { json = ""; }
        _queue = DownloadQueue.FromJson(json);
    }

    /// <summary>Se añadió o cambió una descarga (puede venir de cualquier hilo; se entrega en el principal).</summary>
    public event EventHandler<DownloadItem>? Updated;

    /// <summary>Se eliminó una descarga (id).</summary>
    public event EventHandler<string>? Removed;

    private static string Root => Path.Combine(FileSystem.AppDataDirectory, "downloads");

    private static string DirOf(DownloadItem item) => Path.Combine(Root, item.FolderName);

    public IReadOnlyList<DownloadItem> Items
    {
        get { lock (_gate) return _queue.Items.ToList(); }
    }

    public DownloadItem? Find(MediaItem item, Episode? episode)
    {
        lock (_gate) return _queue.Find(item, episode?.Season, episode?.Number);
    }

    /// <summary>La descarga terminada de ese título y episodio, si el archivo sigue existiendo.</summary>
    public DownloadItem? FindPlayable(MediaItem item, int? season, int? episode)
    {
        DownloadItem? found;
        lock (_gate) found = _queue.Find(item, season, episode);
        // La comprobación en disco va fuera del lock para no retener _gate (la llama la interfaz).
        return found is { State: DownloadState.Completed, FilePath: { } path } && File.Exists(path) ? found : null;
    }

    /// <summary>Velocidad actual (bytes/s) de una descarga en curso.</summary>
    public long SpeedOf(string id) => _speeds.TryGetValue(id, out var s) ? s : 0;

    /// <summary>Hay descargas en cola que esperan porque la red actual no está permitida (WiFi, o sin conexión).</summary>
    public bool WaitingForNetwork
    {
        get
        {
            lock (_gate) return !CanDownloadNow() && _queue.Items.Any(i => i.State is DownloadState.Queued or DownloadState.Downloading);
        }
    }

    public bool WifiOnly => _settings.Current.DownloadOnlyOnWifi;

    /// <summary>Se llama al abrir la app: retoma lo pendiente, vigila la red y limpia carpetas huérfanas.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        try { Connectivity.Current.ConnectivityChanged += (_, _) => OnNetworkChanged(); } catch (Exception) { }
        _settings.Changed += (_, _) => OnNetworkChanged();
        Task.Run(() =>
        {
            try
            {
                HashSet<string> known;
                lock (_gate) known = _queue.Items.Select(i => i.FolderName).ToHashSet();
                if (Directory.Exists(Root))
                    foreach (var dir in Directory.EnumerateDirectories(Root))
                        if (!known.Contains(Path.GetFileName(dir))) try { Directory.Delete(dir, true); } catch { }
            }
            catch (Exception) { }
        });
        Kick();
    }

    /// <summary>
    /// Pone en cola la descarga con la mejor fuente de la lista (ya ordenada) y las siguientes como respaldo.
    /// </summary>
    /// <returns>false si ya había una descarga de ese título y episodio, o no hay fuentes descargables.</returns>
    public bool Enqueue(MediaItem item, Episode? episode, IReadOnlyList<StreamCandidate> ranked, IReadOnlyList<SubtitleTrack> subtitles)
    {
        var usable = ranked.Where(IsDownloadable).ToList();
        if (usable.Count == 0) return false;
        DownloadItem? added;
        string? staleDir = null;
        lock (_gate)
        {
            var download = new DownloadItem
            {
                Id = DownloadQueue.KeyFor(item, episode?.Season, episode?.Number),
                Item = item, Season = episode?.Season, Episode = episode?.Number, EpisodeTitle = episode?.Title,
                Source = usable[0], Alternatives = usable.Skip(1).Take(MaxAlternatives).ToList(), Subtitles = subtitles.Take(MaxSubtitles).ToList(),
            };
            // Una fallida se reintenta desde cero: se descarta lo que hubiera quedado.
            if (_queue.Find(download.Id) is { State: DownloadState.Failed } old) staleDir = DirOf(old);
            added = _queue.Enqueue(download, DateTime.UtcNow) ? _queue.Find(download.Id) : null;
        }
        if (staleDir is not null) DiscardAside(staleDir);
        if (added is null) return false;
        Changed(added, force: true);
        Kick();
        return true;
    }

    /// <summary>Lo que un resolvedor de descargas soporta, o enlaces directos de video (no HLS/DASH, que no se pueden guardar como un archivo).</summary>
    private bool IsDownloadable(StreamCandidate c) =>
        _resolvers.CanDownload(c) || (!string.IsNullOrEmpty(c.Url) && PlaybackRequest.Guess(c.Url) == StreamKind.Progressive);

    public void Pause(string id)
    {
        DownloadItem? item;
        Run? run;
        lock (_gate)
        {
            if (!_queue.Pause(id, DateTime.UtcNow)) return;
            item = _queue.Find(id);
            _runs.TryGetValue(id, out run);
        }
        if (run is not null) Task.Run(run.Cts.Cancel); // cancelar ejecuta callbacks del resolvedor: fuera del hilo principal
        Changed(item, force: true);
        Kick();
    }

    public void Resume(string id)
    {
        DownloadItem? item;
        lock (_gate)
        {
            if (!_queue.Resume(id, DateTime.UtcNow)) return;
            item = _queue.Find(id);
        }
        Changed(item, force: true);
        Kick();
    }

    /// <summary>Cancela la descarga si está en curso y borra todo lo guardado (video, parciales y subtítulos).</summary>
    public async Task RemoveAsync(string id)
    {
        Run? run;
        DownloadItem? item;
        lock (_gate)
        {
            item = _queue.Find(id);
            if (item is null) return;
            _queue.Remove(id);
            _runs.TryGetValue(id, out run);
        }
        if (run is not null) await Task.Run(run.Cts.Cancel);
        if (run is not null) try { await run.Task.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
        await Task.Run(() => TryDelete(DirOf(item)));
        _speeds.TryRemove(id, out _);
        Save(force: true);
        MainThread.BeginInvokeOnMainThread(() => Removed?.Invoke(this, id));
        Kick();
    }

    // ── Cola ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Red permitida ahora: con conexión y, si el ajuste está activo, solo WiFi o cable. Llamar con <c>_gate</c> o sin estado compartido.</summary>
    private bool CanDownloadNow()
    {
        try
        {
            if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet) return false;
            if (!_settings.Current.DownloadOnlyOnWifi) return true;
            var profiles = Connectivity.Current.ConnectionProfiles;
            return profiles.Contains(ConnectionProfile.WiFi) || profiles.Contains(ConnectionProfile.Ethernet);
        }
        catch (Exception) { return true; }
    }

    private void OnNetworkChanged() => Task.Run(OnNetworkChangedCore);

    private void OnNetworkChangedCore()
    {
        var changed = new List<DownloadItem>();
        List<Run> stop = new();
        lock (_gate)
        {
            if (!CanDownloadNow())
                foreach (var d in _queue.Items.Where(i => i.State == DownloadState.Downloading).ToList())
                {
                    _queue.Requeue(d.Id, DateTime.UtcNow);
                    if (_runs.TryGetValue(d.Id, out var run)) stop.Add(run);
                }
            changed.AddRange(_queue.Items.Where(i => i.State == DownloadState.Queued));
        }
        foreach (var r in stop) r.Cts.Cancel();
        foreach (var d in changed) Changed(d, force: true); // refresca "esperando WiFi"
        Kick();
    }

    /// <summary>Arranca lo que toque según la cola, la red y el máximo de descargas simultáneas.</summary>
    private void Kick() => Task.Run(KickNow);

    private void KickNow()
    {
        List<(DownloadItem Item, Run Run)> launch = new();
        lock (_gate)
        {
            if (CanDownloadNow())
                foreach (var d in _queue.NextToStart())
                {
                    if (_runs.ContainsKey(d.Id)) continue; // la ejecución anterior aún está cerrándose: lo retoma al terminar
                    _queue.Start(d.Id, DateTime.UtcNow);
                    var run = new Run(new CancellationTokenSource());
                    _runs[d.Id] = run;
                    launch.Add((_queue.Find(d.Id)!, run));
                }
        }
        foreach (var (item, run) in launch)
        {
            Changed(item, force: true);
            run.Task = Task.Run(() => ExecuteAsync(item.Id, run));
        }
        UpdateForeground(force: true);
    }

    private async Task ExecuteAsync(string id, Run run)
    {
        var ct = run.Cts.Token;
        try
        {
            while (true)
            {
                DownloadItem current;
                lock (_gate) current = _queue.Find(id) ?? throw new OperationCanceledException();
                try
                {
                    EnsureSpace(current);
                    var path = await DownloadSourceAsync(current, ct);
                    await FetchSubtitlesAsync(current, ct);
                    DownloadItem? done;
                    lock (_gate)
                    {
                        _queue.Complete(id, path, DateTime.UtcNow);
                        done = _queue.Find(id);
                    }
                    Changed(done, force: true);
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (SpaceException)
                {
                    Fail(id, LocalizationManager.T("Downloads_NoSpace"));
                    return;
                }
                catch (Exception ex) when (ex is StreamResolveException or HttpRequestException or UnsupportedSourceException)
                {
                    // Esta fuente no responde: se pasa a la siguiente (sin lo parcial de la anterior).
                    StreamCandidate? next;
                    lock (_gate) next = _queue.NextSource(id, DateTime.UtcNow);
                    if (next is null)
                    {
                        Fail(id, LocalizationManager.T("Downloads_SourceFailed"));
                        return;
                    }
                    TryDelete(DirOf(current));
                    Changed(Get(id), force: true);
                }
                catch (Exception ex)
                {
                    Fail(id, ex is IOException ? LocalizationManager.T("Downloads_WriteFailed") : ex.Message);
                    return;
                }
            }
        }
        catch (OperationCanceledException) { /* pausa, eliminación o cambio de red: quien canceló ya dejó el estado */ }
        finally
        {
            _speeds.TryRemove(id, out _);
            lock (_gate) _runs.Remove(id);
            Save(force: true);
            Kick(); // siguiente de la cola, o la misma si se reanudó mientras cerraba
        }
    }

    private DownloadItem? Get(string id)
    {
        lock (_gate) return _queue.Find(id);
    }

    private void Fail(string id, string message)
    {
        DownloadItem? item;
        lock (_gate)
        {
            _queue.Fail(id, message, DateTime.UtcNow);
            item = _queue.Find(id);
        }
        Changed(item, force: true);
    }

    private Task<string> DownloadSourceAsync(DownloadItem item, CancellationToken ct)
    {
        var dir = DirOf(item);
        var id = item.Id;
        if (_resolvers.Find(item.Source) is { } resolver && string.IsNullOrEmpty(item.Source.Url))
        {
            var progress = new Progress<DownloadProgress>(s => OnProgress(id, s.BytesDone, s.BytesTotal, s.BytesPerSecond));
            return Task.Run(() => resolver.DownloadAsync(item.Source, Path.Combine(dir, "_work"), dir, progress, ct), CancellationToken.None);
        }
        return DownloadHttpAsync(item, dir, ct);
    }

    // ── HTTP directo ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Baja el archivo a <c>video.ext.part</c> (reanudando con Range si ya hay parte) y lo renombra al terminar.</summary>
    private async Task<string> DownloadHttpAsync(DownloadItem item, string dir, CancellationToken ct)
    {
        var source = item.Source;
        var url = source.Url!;
        if (PlaybackRequest.Guess(url) != StreamKind.Progressive) throw new UnsupportedSourceException();

        Directory.CreateDirectory(dir);
        var final = Path.Combine(dir, "video" + VideoExtension(source));
        if (File.Exists(final)) return final;
        var part = final + ".part";
        var existing = File.Exists(part) ? new FileInfo(part).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var h in source.Headers) request.Headers.TryAddWithoutValidation(h.Key, h.Value);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
        {
            File.Move(part, final, true); // ya estaba completo
            return final;
        }
        response.EnsureSuccessStatusCode();

        // 206 continúa el parcial; 200 es el archivo entero (el servidor ignoró el Range): se empieza de cero.
        var resumed = response.StatusCode == System.Net.HttpStatusCode.PartialContent && existing > 0;
        var start = resumed ? existing : 0;
        var total = response.Content.Headers.ContentRange?.Length
            ?? (response.Content.Headers.ContentLength is { } len ? len + start : 0);

        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 81920, true))
        {
            var buffer = new byte[81920];
            var done = start;
            var windowStart = DateTime.UtcNow;
            long windowBytes = 0;
            OnProgress(item.Id, done, total, 0);
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                windowBytes += read;
                var elapsed = DateTime.UtcNow - windowStart;
                if (elapsed >= RaiseEvery)
                {
                    OnProgress(item.Id, done, total, (long)(windowBytes / elapsed.TotalSeconds));
                    windowStart = DateTime.UtcNow;
                    windowBytes = 0;
                }
            }
            if (total > 0 && done < total) throw new IOException("Conexión interrumpida antes de terminar."); // "Reanudar" continúa con Range
            OnProgress(item.Id, done, Math.Max(total, done), 0);
        }
        File.Move(part, final, true);
        return final;
    }

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".webm", ".mov", ".ts",
    };

    private static string VideoExtension(StreamCandidate source)
    {
        foreach (var name in new[] { source.FileName, source.Url?.Split('?', '#')[0] })
        {
            var ext = Path.GetExtension(name ?? "").ToLowerInvariant();
            if (VideoExtensions.Contains(ext)) return ext;
        }
        return ".mp4";
    }

    // ── Subtítulos ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Guarda los subtítulos externos junto al video. Es opcional: un fallo no estropea la descarga.</summary>
    private async Task FetchSubtitlesAsync(DownloadItem item, CancellationToken ct)
    {
        if (item.Subtitles.Count == 0 || item.LocalSubtitles.Count > 0) return;
        var dir = DirOf(item);
        var local = new List<SubtitleTrack>();
        foreach (var track in item.Subtitles.Take(MaxSubtitles))
        {
            try
            {
                var bytes = await _http.GetByteArrayAsync(track.Url, ct);
                var ext = track.MimeType.Contains("vtt", StringComparison.OrdinalIgnoreCase) ? ".vtt" : ".srt";
                var path = Path.Combine(dir, $"sub_{local.Count}{ext}");
                await File.WriteAllBytesAsync(path, bytes, ct);
                local.Add(track with { Url = new Uri(path).AbsoluteUri });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* este subtítulo no se pudo bajar; los demás sí */ }
        }
        lock (_gate) _queue.SetLocalSubtitles(item.Id, local);
    }

    // ── Espacio ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Falla si no cabe lo que falta por descargar (más un 5 %). Sin tamaño conocido se exigen 600 MB.</summary>
    private static void EnsureSpace(DownloadItem item)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var size = item.Source.SizeBytes is > 0 ? item.Source.SizeBytes.Value : UnknownSizeReserve;
            var needed = (long)(Math.Max(0, size - item.BytesDone) * 1.05);
#if ANDROID
            using var stat = new global::Android.OS.StatFs(Root);
            var free = stat.AvailableBytes;
#else
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Root))!).AvailableFreeSpace;
#endif
            if (free < needed) throw new SpaceException();
        }
        catch (SpaceException) { throw; }
        catch (Exception) { /* sin dato de espacio se intenta igual */ }
    }

    // ── Avance, guardado y notificación ──────────────────────────────────────────────────────────────────────────

    private void OnProgress(string id, long done, long total, long rate)
    {
        _speeds[id] = rate;
        DownloadItem? item;
        lock (_gate)
        {
            if (!_queue.Report(id, done, total)) return;
            _dirty = true;
            item = _queue.Find(id);
        }
        Changed(item, force: false);
        Save(force: false);
        UpdateForeground(force: false);
    }

    /// <summary>Avisa a la interfaz; el avance frecuente se limita a ~1 por segundo y descarga.</summary>
    private void Changed(DownloadItem? item, bool force)
    {
        if (item is null) return;
        var now = DateTime.UtcNow;
        if (!force && _lastRaise.TryGetValue(item.Id, out var last) && now - last < RaiseEvery) return;
        _lastRaise[item.Id] = now;
        if (force) { _dirty = true; Save(force: true); }
        MainThread.BeginInvokeOnMainThread(() => Updated?.Invoke(this, item));
    }

    /// <summary>Marca el guardado y lo hace en un hilo del pool (JSON + Preferences): quien llame, aunque sea la interfaz, no espera.</summary>
    private void Save(bool force)
    {
        lock (_gate)
        {
            if (!_dirty || (!force && DateTime.UtcNow - _lastSave < SaveEvery)) return;
            _dirty = false;
            _lastSave = DateTime.UtcNow;
        }
        Task.Run(() =>
        {
            // _saveGate conserva el orden de las escrituras; el JSON se toma al escribir, así que siempre es el más reciente.
            lock (_saveGate)
            {
                string json;
                lock (_gate) json = _queue.ToJson();
                try { Preferences.Default.Set(Key, json); }
                catch (Exception) { }
            }
        });
    }

    /// <summary>Muestra u oculta la notificación de primer plano según haya descargas en curso.</summary>
    private void UpdateForeground(bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && now - _lastNotify < NotifyEvery) return;
        _lastNotify = now;

        List<DownloadItem> active;
        lock (_gate) active = _queue.Items.Where(i => i.State == DownloadState.Downloading).ToList();
        if (active.Count == 0)
        {
#if ANDROID
            DownloadForegroundService.Hide();
#endif
            return;
        }
#if ANDROID
        var first = active[0];
        var name = first.IsEpisode ? $"{first.Item.Title} · T{first.Season}:E{first.Episode}" : first.Item.Title;
        var percent = first.BytesTotal > 0 ? (int)(first.Fraction * 100) : -1;
        var text = percent >= 0 ? $"{name} · {percent}%" : name;
        DownloadForegroundService.Show(LocalizationManager.T("Downloads_Title"), text, percent);
#endif
    }

    /// <summary>Aparta la carpeta (renombrar es instantáneo) y la borra en segundo plano; la ruta original queda libre al momento.</summary>
    private static void DiscardAside(string dir)
    {
        var aside = dir.TrimEnd(Path.DirectorySeparatorChar) + ".del-" + Guid.NewGuid().ToString("N");
        try { if (Directory.Exists(dir)) Directory.Move(dir, aside); else return; }
        catch (Exception) { aside = dir; }
        Task.Run(() => TryDelete(aside)); // las carpetas ".del-*" huérfanas las limpia Start()
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (Exception) { }
    }
}
