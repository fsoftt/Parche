using AndroidX.Media3.Common;
using AndroidX.Media3.DataSource;
using AndroidX.Media3.ExoPlayer;
using AndroidX.Media3.ExoPlayer.Source;
using AndroidX.Media3.UI;
using Microsoft.Maui.Handlers;
using Parche.App.Controls;
using Parche.Core.Models;
using ExoMediaItem = AndroidX.Media3.Common.MediaItem;

namespace Parche.App.Platforms.Android;

/// <summary>Implementa <see cref="NativeVideoView"/> con Media3 ExoPlayer + PlayerView (controles compatibles con D-pad).</summary>
public class NativeVideoViewHandler() : ViewHandler<NativeVideoView, PlayerView>(Mapper)
{
    public static readonly IPropertyMapper<NativeVideoView, NativeVideoViewHandler> Mapper =
        new PropertyMapper<NativeVideoView, NativeVideoViewHandler>(ViewMapper)
        {
            [nameof(NativeVideoView.Stream)] = MapStream,
        };

    private IExoPlayer? _player;
    private Listener? _listener;
    private IDispatcherTimer? _progressTimer;

    protected override PlayerView CreatePlatformView() => new(Context)
    {
        UseController = true,
        ControllerAutoShow = true,
        KeepScreenOn = true,
        Focusable = true,
        FocusableInTouchMode = true,
    };

    protected override void ConnectHandler(PlayerView platformView)
    {
        base.ConnectHandler(platformView);
        platformView.SetShowSubtitleButton(true);
        platformView.ControllerVisibility += OnControllerVisibility;
        platformView.SetShutterBackgroundColor(global::Android.Graphics.Color.Black.ToArgb());
        platformView.RequestFocus();
    }

    protected override void DisconnectHandler(PlayerView platformView)
    {
        platformView.ControllerVisibility -= OnControllerVisibility;
        ReleasePlayer();
        platformView.Player = null;
        base.DisconnectHandler(platformView);
    }

    private void OnControllerVisibility(object? sender, PlayerView.ControllerVisibilityEventArgs e) =>
        VirtualView?.RaiseControllerVisibility(e.P0 == 0); // View.VISIBLE

    private static void MapStream(NativeVideoViewHandler handler, NativeVideoView view)
    {
        if (view.Stream is { } stream) handler.Play(stream);
        else handler.ReleasePlayer();
    }

    private void Play(PlaybackRequest stream)
    {
        ReleasePlayer();

        var headers = stream.Headers.Where(h => !h.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key, h => h.Value);
        var http = new DefaultHttpDataSource.Factory()
            .SetAllowCrossProtocolRedirects(true)!
            .SetUserAgent(stream.Headers.TryGetValue("User-Agent", out var ua) ? ua : Parche.App.Services.HttpDefaults.UserAgent)!;
        http.SetDefaultRequestProperties(headers);

        // DefaultDataSource lee https con el factory http y también archivos locales (descargas).
        var mediaSourceFactory = new DefaultMediaSourceFactory(new DefaultDataSource.Factory(Context, http));
        _player = new ExoPlayerBuilder(Context)
            .SetMediaSourceFactory(mediaSourceFactory)!
            .SetSeekBackIncrementMs(10_000)!
            .SetSeekForwardIncrementMs(10_000)!
            .Build()!;
        ApplyPreferredAudio(_player);

        _listener = new Listener(this);
        _player.AddListener(_listener);

        var builder = new ExoMediaItem.Builder().SetUri(stream.Url)!;
        var mime = stream.Kind switch
        {
            StreamKind.Hls => "application/x-mpegURL",
            StreamKind.Dash => "application/dash+xml",
            _ => null,
        };
        if (mime is not null) builder.SetMimeType(mime);

        // Subtítulos externos: aparecen en el selector de pistas del PlayerView.
        var subtitles = new List<ExoMediaItem.SubtitleConfiguration>();
        foreach (var track in stream.Subtitles)
        {
            try
            {
                subtitles.Add(new ExoMediaItem.SubtitleConfiguration.Builder(global::Android.Net.Uri.Parse(track.Url)!)
                    .SetMimeType(track.MimeType)!
                    .SetLanguage(track.Language)!
                    .SetLabel(track.Label)!
                    .Build()!);
            }
            catch (Exception)
            {
                // Un subtítulo mal formado no debe impedir reproducir el video.
            }
        }
        if (subtitles.Count > 0) builder.SetSubtitleConfigurations(subtitles);

        if (stream.StartPositionMs > 0) _player.SetMediaItem(builder.Build()!, stream.StartPositionMs);
        else _player.SetMediaItem(builder.Build()!);
        _player.Prepare();
        _player.PlayWhenReady = true;

        PlatformView.Player = _player;
        PlatformView.RequestFocus();

        _progressTimer = VirtualView.Dispatcher.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromSeconds(5);
        _progressTimer.Tick += (_, _) => ReportProgress();
        _progressTimer.Start();
    }

    /// <summary>Informa posición y duración, salvo que el video aún no haya arrancado.</summary>
    private void ReportProgress()
    {
        if (_player is null || VirtualView is null) return;
        var duration = _player.Duration; // C.TIME_UNSET (negativo) mientras no se conoce
        var position = _player.CurrentPosition;
        if (position <= 0) return;
        VirtualView.RaiseProgress(new PlaybackProgress(position, duration > 0 ? duration : 0));
    }

    /// <summary>
    /// Las fuentes "Multi" traen varias pistas de audio y ExoPlayer elige la primera (casi siempre inglés): se le
    /// indican los idiomas preferidos de los ajustes para que arranque en español/latino si el archivo lo trae.
    /// </summary>
    private void ApplyPreferredAudio(IExoPlayer player)
    {
        var langs = MauiContext?.Services.GetService<Parche.App.Services.SettingsService>()?.Current.PreferredLanguages;
        if (langs is not { Count: > 0 }) return;
        // ExoPlayer compara por idioma base (ISO 639): "es-419" y "es" son la misma pista "spa".
        var codes = langs.Select(l => l.Split('-')[0]).Distinct().ToArray();
        player.TrackSelectionParameters = player.TrackSelectionParameters.BuildUpon()
            .SetPreferredAudioLanguages(codes)!
            .Build()!;
    }

    private void ReleasePlayer()
    {
        _progressTimer?.Stop();
        _progressTimer = null;
        if (_player is null) return;
        ReportProgress();
        if (_listener is not null) _player.RemoveListener(_listener);
        _player.Release();
        _player.Dispose();
        _player = null;
        _listener = null;
    }

    private sealed class Listener(NativeVideoViewHandler handler) : Java.Lang.Object, IPlayerListener
    {
        private const int StateEnded = 4;

        public void OnPlayerError(PlaybackException? error)
        {
            int? status = null;
            string? causeText = null;
            var cause = error?.Cause;
            for (var depth = 0; cause is not null && depth < 10; depth++)
            {
                if (cause is HttpDataSourceInvalidResponseCodeException http)
                {
                    status = http.ResponseCode;
                    causeText = $"HTTP {http.ResponseCode} {http.ResponseMessage}".Trim();
                    break;
                }
                causeText ??= cause.ToString();
                cause = cause.Cause;
            }

            var message = $"{error?.ErrorCodeName}: {error?.Message}";
            if (causeText is not null) message += $"\n{causeText}";
            handler.VirtualView?.RaiseFailed(new PlaybackFailure(message, error?.ErrorCodeName, status));
        }

        public void OnPlaybackStateChanged(int playbackState)
        {
            if (playbackState == StateEnded)
            {
                handler.ReportProgress();
                handler.VirtualView?.RaiseEnded();
            }
        }

        /// <summary>Al pausar se guarda la posición enseguida (el usuario puede cerrar la app en pausa).</summary>
        public void OnIsPlayingChanged(bool isPlaying)
        {
            if (!isPlaying) handler.ReportProgress();
        }
    }
}
