using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Parche.App.Controls;
using Parche.App.Localization;
using Parche.App.Services;
using Parche.Core;
using Parche.Core.Addons;
using Parche.Core.Library;
using Parche.Core.Models;
using Parche.App.Plugins;

namespace Parche.App.ViewModels;

/// <summary>
/// Prepara la reproducción de la fuente elegida: enlace directo (ExoPlayer lo abre tal cual) o referencia
/// (un IStreamResolver la convierte en una URL HTTP local que ExoPlayer lee).
/// </summary>
public partial class PlayerViewModel(StreamResolvers resolvers, WatchProgressService progress) : BaseViewModel, IQueryAttributable
{
    private static readonly TimeSpan OverlayTimeout = TimeSpan.FromSeconds(4);

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _overlayCts;
    private const int MaxAttempts = 4;

    private StreamCandidate? _candidate;
    /// <summary>Siguientes fuentes de la lista, por si la elegida no responde.</summary>
    private IReadOnlyList<StreamCandidate> _fallbacks = Array.Empty<StreamCandidate>();
    private readonly List<string> _attempts = new();
    /// <summary>Video descargado que se reproduce sin red; null si se reproduce la fuente.</summary>
    private string? _localFile;
    private MediaItem? _item;
    private Episode? _episode;
    private IReadOnlyList<SubtitleTrack> _subtitles = Array.Empty<SubtitleTrack>();
    private IResolvedStream? _session;
    private EventHandler<ResolveStatus>? _statusHandler;
    private int _run;

    // Datos para el diagnóstico.
    private PlaybackRequest? _lastRequest;
    private ResolveStatus? _lastStatus;
    private PlaybackFailure? _nativeFailure;
    private readonly Stopwatch _clock = new();
    private TimeSpan? _readyAfter;
    private TimeSpan? _failedAfter;
    private string? _startError;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveVisible))]
    private PlaybackRequest? _stream;

    [ObservableProperty]
    private string _status = LocalizationManager.T("Player_Preparing");

    // Estado del arranque de una fuente que un resolvedor prepara.
    [ObservableProperty]
    private bool _isResolving;

    [ObservableProperty]
    private double _fraction;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private string _speedText = "";

    [ObservableProperty]
    private string _connectionsText = "";

    /// <summary>Chip de esquina durante la reproducción: "↓ 2.3 MB/s · 24 conexiones".</summary>
    [ObservableProperty]
    private string _liveText = "";

    /// <summary>Barra superior (otra fuente / diagnóstico) y chip de estado; se oculta con los controles.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveVisible))]
    private bool _overlayVisible;

    [ObservableProperty]
    private bool _diagnosticsVisible;

    [ObservableProperty]
    private string _diagnosticsText = "";

    public bool LiveVisible => OverlayVisible && IsResolving && Stream is not null;

    partial void OnIsResolvingChanged(bool value) => OnPropertyChanged(nameof(LiveVisible));

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("candidate", out var c) && c is StreamCandidate candidate)
        {
            _candidate = candidate;
            IsResolving = candidate.NeedsResolver;
        }
        _fallbacks = query.TryGetValue("fallbacks", out var f) && f is IReadOnlyList<StreamCandidate> next ? next : Array.Empty<StreamCandidate>();
        if (query.TryGetValue("subtitles", out var s) && s is IReadOnlyList<SubtitleTrack> subs) _subtitles = subs;
        if (query.TryGetValue("title", out var t) && t is string title) Title = title;
        if (query.TryGetValue("item", out var i) && i is MediaItem item) _item = item;
        _episode = query.TryGetValue("episode", out var e) ? e as Episode : null;
        _localFile = query.TryGetValue("localFile", out var lf) ? lf as string : null;
        if (_localFile is not null) IsResolving = false; // se reproduce el archivo descargado, sin resolvedor
    }

    public Task StartAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var run = ++_run;

        Stream = null;
        Error = null;
        _nativeFailure = null;
        _startError = null;
        _lastStatus = null;
        _readyAfter = _failedAfter = null;
        DiagnosticsVisible = false;
        Fraction = 0;
        ProgressText = SpeedText = ConnectionsText = LiveText = "";
        Status = LocalizationManager.T("Player_Preparing");
        _clock.Restart();

        return RunAsync(async () =>
        {
            if (_candidate is null) throw new InvalidOperationException(LocalizationManager.T("Player_CannotPlay"));
            await DetachSessionAsync();

            if (_localFile is not null)
            {
                // Descarga terminada: ExoPlayer lee el archivo (y sus subtítulos guardados) sin usar la red.
                if (!File.Exists(_localFile)) throw new InvalidOperationException(LocalizationManager.T("Downloads_FileMissing"));
                _lastRequest = new PlaybackRequest
                {
                    Url = new Uri(_localFile).AbsoluteUri,
                    Kind = StreamKind.Progressive,
                    Subtitles = _subtitles.Where(sub => !sub.Url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || File.Exists(new Uri(sub.Url).LocalPath)).ToList(),
                    Title = Title,
                    StartPositionMs = ResumeMs(),
                };
                _readyAfter = _clock.Elapsed;
                Status = "";
                Stream = _lastRequest;
                ShowOverlay();
                return;
            }

            if (!string.IsNullOrEmpty(_candidate.Url))
            {
                // Enlace directo: ExoPlayer lo abre con las cabeceras del addon.
                _lastRequest = ParcheEngine.CreatePlayback(_candidate, _subtitles, Title) with { StartPositionMs = ResumeMs() };
                _readyAfter = _clock.Elapsed;
                Status = "";
                Stream = _lastRequest;
                ShowOverlay();
                return;
            }

            if (!resolvers.CanResolve(_candidate)) throw new InvalidOperationException(LocalizationManager.T("Player_CannotPlay"));

            // Referencia por resolver: Progress<T> captura el contexto de la UI, así que OnResolveStatus corre en el hilo principal.
            // La disponibilidad que informan los addons suele estar desactualizada: si una fuente no responde, se prueba
            // sola la siguiente de la lista (hasta MaxAttempts) en vez de dejar al usuario esperando.
            var progress = new Progress<ResolveStatus>(st => { if (run == _run) OnResolveStatus(st); });
            IResolvedStream? session = null;
            var queue = new Queue<StreamCandidate>(_fallbacks.Where(f => resolvers.CanResolve(f) && !ReferenceEquals(f, _candidate)).Take(MaxAttempts - 1));
            _attempts.Clear();
            while (session is null)
            {
                try
                {
                    // Mientras esta fuente carga, la siguiente va conectando: si esta falla, el cambio es casi inmediato.
                    // Se lanza un instante después porque StartAsync descarta cualquier precarga ajena al empezar.
                    if (queue.TryPeek(out var next))
                        _ = Task.Delay(1000, ct).ContinueWith(_ => resolvers.PreloadAlternateAsync(next),
                            ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
                    session = await resolvers.ResolveAsync(_candidate, progress, ct);
                    _ = resolvers.CancelPreloadAsync();
                }
                catch (StreamResolveException ex)
                {
                    // El detalle técnico queda en Diagnóstico; al usuario se le habla de la fuente.
                    _attempts.Add($"{_candidate.Quality} {_candidate.Provider} {_candidate.InfoHash}: {ex.Message}");
                    _startError = ex.Message;
                    _failedAfter = _clock.Elapsed;
                    if (ct.IsCancellationRequested || run != _run || queue.Count == 0)
                        throw new InvalidOperationException(LocalizationManager.T("Player_SourceUnavailable"), ex);
                    _candidate = queue.Dequeue();
                    Fraction = 0;
                    ProgressText = SpeedText = ConnectionsText = "";
                    Status = LocalizationManager.T("Player_TryingAnother");
                }
            }
            if (ct.IsCancellationRequested || run != _run)
            {
                await resolvers.StopAsync();
                return;
            }

            _session = session;
            _statusHandler = (_, st) => MainThread.BeginInvokeOnMainThread(() => { if (run == _run) OnResolveStatus(st); });
            session.StatusChanged += _statusHandler;

            _lastRequest = new PlaybackRequest
            {
                Url = session.Url.ToString(),
                Kind = StreamKind.Progressive,
                Subtitles = _subtitles,
                Title = Title,
                StartPositionMs = ResumeMs(),
            };
            _readyAfter = _clock.Elapsed;
            Status = "";
            Stream = _lastRequest;
            ShowOverlay();
        });
    }

    /// <summary>Continúa donde se dejó este título (y episodio), con cualquier fuente; 0 si no hay registro.</summary>
    private long ResumeMs() => _item is null ? 0 : progress.Find(_item, _episode)?.PositionMs ?? 0;

    /// <summary>El reproductor informó su avance: se guarda para "Continuar viendo" junto con la fuente usada.</summary>
    public void OnProgress(PlaybackProgress p)
    {
        if (_item is null) return;
        progress.Report(new WatchEntry
        {
            Item = _item,
            Season = _episode?.Season,
            Episode = _episode?.Number,
            EpisodeTitle = _episode?.Title,
            PositionMs = p.PositionMs,
            DurationMs = p.DurationMs,
            UpdatedUtc = DateTime.UtcNow,
            Source = _candidate,
        });
    }

    private void OnResolveStatus(ResolveStatus st)
    {
        _lastStatus = st;
        Fraction = Math.Clamp(st.Progress, 0, 1);
        ProgressText = $"{st.Progress * 100:0}%";
        var speed = LocalizationManager.T("Player_Speed", st.BytesPerSecond / 1048576.0);
        var connections = LocalizationManager.T("Player_Connections", st.Connections);
        SpeedText = speed;
        ConnectionsText = connections;
        LiveText = $"{speed}  ·  {connections}";

        if (Stream is null)
        {
            Status = st.Stage switch
            {
                ResolveStage.Buffering => LocalizationManager.T("Player_PhaseBuffering"),
                ResolveStage.Connecting when st.Connections > 0 => LocalizationManager.T("Player_PhaseMetadata"),
                ResolveStage.Ready => LocalizationManager.T("Player_PhaseBuffering"),
                _ => LocalizationManager.T("Player_PhaseSearching"),
            };
        }
    }

    private async Task DetachSessionAsync()
    {
        if (_session is not null && _statusHandler is not null) _session.StatusChanged -= _statusHandler;
        _session = null;
        _statusHandler = null;
        await resolvers.StopAsync();
    }

    /// <summary>Cierra el reproductor y la sesión del resolvedor (se llama al salir de la página).</summary>
    public void Stop()
    {
        _run++;
        _cts?.Cancel();
        _overlayCts?.Cancel();
        Stream = null; // el reproductor informa su última posición al liberarse
        progress.Flush();
        OverlayVisible = false;
        _ = DetachSessionAsync();
    }

    /// <summary>
    /// El reproductor nativo falló (p.ej. el enlace caducó o responde 403). Se muestra el error con
    /// Reintentar / Elegir otra fuente / Diagnóstico para poder leer la causa.
    /// </summary>
    public Task OnNativeFailedAsync(PlaybackFailure failure)
    {
        _nativeFailure = failure;
        _failedAfter = _clock.Elapsed;
        Stream = null; // libera el reproductor y muestra la vista de error
        Status = "";
        var http = failure.HttpStatus is { } s ? $" (HTTP {s})" : "";
        Error = LocalizationManager.T("Player_NativeFailed") + http;
        _overlayCts?.Cancel();
        OverlayVisible = false;
        return Task.CompletedTask;
    }

    /// <summary>Muestra la barra superior y la oculta a los 4 s (salvo que haya un panel abierto).</summary>
    public void ShowOverlay()
    {
        if (Stream is null) return;
        OverlayVisible = true;
        _overlayCts?.Cancel();
        var cts = _overlayCts = new CancellationTokenSource();
        _ = Task.Delay(OverlayTimeout, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled && !DiagnosticsVisible) MainThread.BeginInvokeOnMainThread(() => OverlayVisible = false);
        }, TaskScheduler.Default);
    }

    /// <summary>Los controles nativos aparecieron o se ocultaron.</summary>
    public void OnControllerVisibility(bool visible)
    {
        if (visible) ShowOverlay();
        else if (!DiagnosticsVisible) OverlayVisible = false;
    }

    [RelayCommand]
    private Task RetryAsync() => StartAsync();

    /// <summary>Cancela el arranque (o cierra el reproductor) y vuelve a la lista de fuentes.</summary>
    [RelayCommand]
    private async Task BackToSourcesAsync()
    {
        Stop();
        await Shell.Current.GoToAsync("..");
    }

    [RelayCommand]
    private void ShowDiagnostics()
    {
        _overlayCts?.Cancel();
        DiagnosticsText = BuildDiagnostics();
        DiagnosticsVisible = true;
    }

    [RelayCommand]
    private void CloseDiagnostics()
    {
        DiagnosticsVisible = false;
        ShowOverlay();
    }

    [RelayCommand]
    private async Task CopyDiagnosticsAsync()
    {
        try { await Clipboard.Default.SetTextAsync(DiagnosticsText); }
        catch (Exception) { /* el portapapeles puede no estar disponible; el texto sigue visible */ }
    }

    private static string Trunc(string? s, int max = 90) =>
        string.IsNullOrEmpty(s) ? "-" : s.Length <= max ? s : s[..max] + "…";

    private string BuildDiagnostics()
    {
        var sb = new StringBuilder();
        void Line(string label, object? value) => sb.Append(label).Append(": ").AppendLine(value?.ToString() ?? "-");

        var c = _candidate;
        Line("Title", Title);
        Line("Addon", c?.Addon);
        Line("Candidate", Trunc(c?.Title.Replace('\n', ' ')));
        Line("Quality", c?.Quality);
        Line("Languages", c is null ? null : string.Join(", ", c.Languages));
        Line("Size", Format.Size(c?.SizeBytes));
        Line("Popularity", c?.Popularity);
        Line("Direct / instant", c is null ? null : $"{c.IsDirect} / {c.IsInstant}");
        if (c is { NeedsResolver: true })
        {
            Line("InfoHash", c.InfoHash);
            Line("FileIdx", c.FileIdx);
        }
        else Line("URL", Trunc(c?.Url));

        sb.AppendLine("Headers:");
        var headers = _lastRequest?.Headers ?? c?.Headers;
        if (headers is { Count: > 0 })
            foreach (var h in headers) sb.Append("  ").Append(h.Key).Append(": ").AppendLine(Trunc(h.Value, 60));
        else sb.AppendLine("  -");

        Line("Kind", _lastRequest?.Kind);
        Line("Player URL", Trunc(_lastRequest?.Url));
        Line("Subtitles", _subtitles.Count);

        if (_attempts.Count > 0)
        {
            sb.AppendLine("Failed sources:");
            foreach (var a in _attempts) sb.Append("  ").AppendLine(Trunc(a, 160));
        }

        sb.AppendLine("Source session:");
        if (_lastStatus is { } st)
        {
            Line("  Stage", st.Stage);
            Line("  Progress", $"{st.Progress * 100:0.0}%");
            Line("  Down", $"{st.BytesPerSecond / 1048576.0:0.00} MB/s");
            Line("  Connections", st.Connections);
            if (st.Message is { } m) Line("  Message", m);
        }
        else sb.AppendLine("  -");
        if (_startError is { } se) Line("Start error", se);
        if (_session is { } sess) { Line("File", sess.FileName); Line("File size", Format.Size(sess.FileSize)); }

        Line("Ready after", _readyAfter is { } r ? $"{r.TotalSeconds:0.0} s" : null);
        Line("Failed after", _failedAfter is { } f ? $"{f.TotalSeconds:0.0} s" : null);
        Line("Elapsed", $"{_clock.Elapsed.TotalSeconds:0.0} s");

        sb.AppendLine("Native player error:");
        if (_nativeFailure is { } nf)
        {
            Line("  Code", nf.ErrorCodeName);
            Line("  HTTP status", nf.HttpStatus);
            sb.Append("  ").AppendLine(nf.Message.Replace("\n", "\n  "));
        }
        else sb.AppendLine("  -");
        return sb.ToString();
    }
}
