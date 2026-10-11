using System.Globalization;
using LibVLCSharp.Platforms.Windows;
using LibVLCSharp.Shared;
using Microsoft.Maui.Handlers;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Parche.App.Controls;
using Parche.App.Services;
using Parche.Core.Models;
using Windows.System;
using VlcMedia = LibVLCSharp.Shared.Media;
using HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment;
using VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment;
using Visibility = Microsoft.UI.Xaml.Visibility;
using VlcCore = LibVLCSharp.Shared.Core;
using Button = Microsoft.UI.Xaml.Controls.Button;
using ColumnDefinition = Microsoft.UI.Xaml.Controls.ColumnDefinition;
using Colors = Microsoft.UI.Colors;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using GradientStop = Microsoft.UI.Xaml.Media.GradientStop;
using Grid = Microsoft.UI.Xaml.Controls.Grid;
using GridLength = Microsoft.UI.Xaml.GridLength;
using GridUnitType = Microsoft.UI.Xaml.GridUnitType;
using LinearGradientBrush = Microsoft.UI.Xaml.Media.LinearGradientBrush;
using MenuFlyout = Microsoft.UI.Xaml.Controls.MenuFlyout;
using MenuFlyoutItem = Microsoft.UI.Xaml.Controls.MenuFlyoutItem;
using Orientation = Microsoft.UI.Xaml.Controls.Orientation;
using Slider = Microsoft.UI.Xaml.Controls.Slider;
using SolidColorBrush = Microsoft.UI.Xaml.Media.SolidColorBrush;
using Thickness = Microsoft.UI.Xaml.Thickness;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Parche.App.Platforms.Windows;

/// <summary>
/// Implementa <see cref="NativeVideoView"/> en Windows con LibVLC (LibVLCSharp.WinUI: el video se dibuja en un
/// SwapChainPanel, así que los controles XAML se superponen sin problemas de "airspace"). LibVLC abre MKV/MP4 con
/// H.264/HEVC, varias pistas de audio y subtítulos. Los controles (play/pausa,
/// barra de avance, volumen, pistas, pantalla completa) y los atajos de teclado son propios porque VLC no trae UI.
/// </summary>
public class NativeVideoViewHandler() : ViewHandler<NativeVideoView, ContentControl>(Mapper)
{
    public static readonly IPropertyMapper<NativeVideoView, NativeVideoViewHandler> Mapper =
        new PropertyMapper<NativeVideoView, NativeVideoViewHandler>(ViewMapper)
        {
            [nameof(NativeVideoView.Stream)] = MapStream,
        };

    private static readonly TimeSpan HideAfter = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SeekStep = TimeSpan.FromSeconds(10);

    private VideoView? _video;
    private LibVLC? _libVlc;
    private VlcMediaPlayer? _player;
    private PlaybackRequest? _pending;
    private int _generation;

    private Grid? _bar;
    private Button? _playButton;
    private Button? _muteButton;
    private Slider? _seek;
    private Slider? _volume;
    private TextBlock? _time;
    private ProgressRing? _ring;
    private MenuFlyout? _audioMenu;
    private MenuFlyout? _subtitleMenu;
    private DispatcherQueueTimer? _hideTimer;
    private IDispatcherTimer? _progressTimer;
    private bool _barVisible = true;
    private bool _pointerOverBar;
    private bool _menuOpen;
    private bool _dragging;
    private bool _settingSeek;
    private bool _ended;
    private bool _started;

    protected override ContentControl CreatePlatformView()
    {
        _video = new VideoView { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        _video.Initialized += OnVideoInitialized;

        var root = new Grid { Background = new SolidColorBrush(Colors.Black) };
        root.Children.Add(_video);
        _ring = new ProgressRing { IsActive = false, Width = 48, Height = 48, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        root.Children.Add(_ring);
        root.Children.Add(BuildBar());
        root.Tapped += (_, _) => { TogglePlay(); Activity(); };
        root.DoubleTapped += (_, _) => FullScreen.Toggle();
        root.PointerMoved += (_, _) => Activity();

        return new ContentControl
        {
            Content = root,
            IsTabStop = true,
            UseSystemFocusVisuals = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Colors.Black),
        };
    }

    protected override void ConnectHandler(ContentControl platformView)
    {
        base.ConnectHandler(platformView);
        // handledEventsToo: los botones y el slider marcan la tecla como atendida, pero los atajos deben funcionar igual.
        platformView.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
        platformView.Loaded += (_, _) => platformView.Focus(FocusState.Programmatic);
        _hideTimer = platformView.DispatcherQueue.CreateTimer();
        _hideTimer.Interval = HideAfter;
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => HideBarIfPlaying();
    }

    protected override void DisconnectHandler(ContentControl platformView)
    {
        _hideTimer?.Stop();
        ReleasePlayer();
        if (_video is not null) _video.Initialized -= OnVideoInitialized;
        var libVlc = _libVlc;
        _libVlc = null;
        if (libVlc is not null) Task.Run(libVlc.Dispose);
        FullScreen.Exit();
        base.DisconnectHandler(platformView);
    }

    private static void MapStream(NativeVideoViewHandler handler, NativeVideoView view)
    {
        if (view.Stream is { } stream) handler.Play(stream);
        else handler.ReleasePlayer();
    }

    // ── LibVLC ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>El SwapChainPanel ya existe: recién ahora se puede crear LibVLC (necesita las opciones del swap chain).</summary>
    private void OnVideoInitialized(object? sender, InitializedEventArgs e)
    {
        try
        {
            VlcCore.Initialize();
            _libVlc = new LibVLC(enableDebugLogs: false, e.SwapChainOptions);
        }
        catch (Exception ex)
        {
            VirtualView?.RaiseFailed(new PlaybackFailure($"No se pudo iniciar LibVLC: {ex.Message}", "LIBVLC_INIT"));
            return;
        }
        if (_pending is { } pending)
        {
            _pending = null;
            Play(pending);
        }
    }

    private void Play(PlaybackRequest stream)
    {
        ReleasePlayer();
        if (_libVlc is null)
        {
            _pending = stream; // LibVLC aún no está listo: se reproduce al inicializar
            return;
        }

        var generation = ++_generation;
        _ended = false;
        _started = false;
        var player = new VlcMediaPlayer(_libVlc) { EnableHardwareDecoding = true };
        _player = player;
        _video!.MediaPlayer = player;

        // Los eventos de VLC llegan en hilos propios: todo lo que toca la UI pasa por OnUi.
        player.Playing += (_, _) => OnUi(() => OnPlaying(generation, stream));
        player.Paused += (_, _) => OnUi(() => { UpdatePlayIcon(); ShowBar(); ReportProgress(); });
        player.Stopped += (_, _) => OnUi(UpdatePlayIcon);
        player.EndReached += (_, _) => OnUi(() => OnEnded(generation));
        player.EncounteredError += (_, _) => OnUi(() => OnError(generation, stream));
        player.LengthChanged += (_, args) => OnUi(() => OnLength(args.Length));
        player.TimeChanged += (_, args) => OnUi(() => OnTime(args.Time));
        player.Buffering += (_, args) => OnUi(() => SetBuffering(args.Cache < 100));

        var media = Uri.TryCreate(stream.Url, UriKind.Absolute, out var uri) && uri.IsFile
            ? new VlcMedia(_libVlc, uri.LocalPath, FromType.FromPath)
            : new VlcMedia(_libVlc, stream.Url, FromType.FromLocation);

        // VLC solo admite User-Agent y Referer entre las cabeceras HTTP (Origin y demás no se pueden enviar).
        var ua = stream.Headers.FirstOrDefault(h => h.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)).Value
                 ?? HttpDefaults.UserAgent;
        media.AddOption($":http-user-agent={ua}");
        var referer = stream.Headers.FirstOrDefault(h => h.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase) || h.Key.Equals("Referrer", StringComparison.OrdinalIgnoreCase)).Value;
        if (!string.IsNullOrEmpty(referer)) media.AddOption($":http-referrer={referer}");
        media.AddOption(":network-caching=3000");
        if (stream.StartPositionMs > 0)
            media.AddOption($":start-time={(stream.StartPositionMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture)}");

        player.Volume = (int)(_volume?.Value ?? 100);
        player.Play(media);
        media.Dispose(); // el reproductor conserva su propia referencia

        _progressTimer = VirtualView.Dispatcher.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromSeconds(5);
        _progressTimer.Tick += (_, _) => ReportProgress();
        _progressTimer.Start();
        ShowBar();
        PlatformView.Focus(FocusState.Programmatic);
    }

    /// <summary>Al arrancar: agrega los subtítulos externos y elige el audio según los idiomas preferidos de los ajustes.</summary>
    private async void OnPlaying(int generation, PlaybackRequest stream)
    {
        UpdatePlayIcon();
        SetBuffering(false);
        if (generation != _generation || _player is not { } player || _started) return; // solo la primera vez por video
        _started = true;

        foreach (var track in stream.Subtitles)
        {
            try { player.AddSlave(MediaSlaveType.Subtitle, track.Url, false); }
            catch (Exception) { /* un subtítulo mal formado no debe impedir reproducir */ }
        }

        var langs = MauiContext?.Services.GetService<SettingsService>()?.Current.PreferredLanguages;
        if (langs is not { Count: > 0 }) return;
        // Las pistas se descubren unos instantes después de arrancar.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(400);
            if (generation != _generation || _player is not { } p) return;
            // Media.Tracks consulta a LibVLC (puede bloquear): se lee en el pool, no en la interfaz.
            var tracks = await Task.Run(() => p.Media?.Tracks.Where(t => t.TrackType == TrackType.Audio).ToArray());
            if (tracks is not { Length: > 0 }) continue;
            if (PickAudio(tracks, langs) is { } id && id != p.AudioTrack) p.SetAudioTrack(id);
            return;
        }
    }

    /// <summary>Pista cuyo idioma coincide con el primero de los preferidos que exista (por idioma base: "es-419" = "es").</summary>
    private static int? PickAudio(IReadOnlyList<MediaTrack> tracks, IReadOnlyList<string> preferred)
    {
        foreach (var code in preferred.Select(l => l.Split('-')[0]).Distinct())
        {
            string? three = null;
            try { three = CultureInfo.GetCultureInfo(code).ThreeLetterISOLanguageName; } catch (CultureNotFoundException) { }
            foreach (var t in tracks)
            {
                var lang = t.Language;
                if (string.IsNullOrEmpty(lang) || lang == "und") continue;
                if (lang.Equals(code, StringComparison.OrdinalIgnoreCase) || (three is not null && lang.Equals(three, StringComparison.OrdinalIgnoreCase)))
                    return t.Id;
            }
        }
        return null;
    }

    private void OnError(int generation, PlaybackRequest stream)
    {
        if (generation != _generation) return;
        SetBuffering(false);
        VirtualView?.RaiseFailed(new PlaybackFailure($"VLC no pudo reproducir el video ({stream.Url})", "LIBVLC_ERROR"));
    }

    private void OnEnded(int generation)
    {
        if (generation != _generation || _ended) return;
        _ended = true;
        ReportProgress();
        VirtualView?.RaiseEnded();
    }

    /// <summary>Informa posición y duración, salvo que el video aún no haya arrancado.</summary>
    private void ReportProgress()
    {
        if (_player is not { } player || VirtualView is null) return;
        var position = player.Time;
        if (position <= 0) return;
        VirtualView.RaiseProgress(new PlaybackProgress(position, Math.Max(0, player.Length)));
    }

    private void ReleasePlayer()
    {
        _generation++;
        _progressTimer?.Stop();
        _progressTimer = null;
        var player = _player;
        if (player is null) return;
        ReportProgress();
        _player = null;
        if (_video is not null) _video.MediaPlayer = null;
        // Detener/liberar desde el hilo de la UI puede bloquear con LibVLC: se hace aparte.
        Task.Run(() =>
        {
            try { player.Stop(); } catch (Exception) { }
            player.Dispose();
        });
    }

    private void OnUi(Action action) => PlatformView?.DispatcherQueue.TryEnqueue(() => action());

    // ── Controles ────────────────────────────────────────────────────────────────────────────────────────────────

    private Grid BuildBar()
    {
        _playButton = IconButton("");
        _playButton.Click += (_, _) => { TogglePlay(); Refocus(); };

        _seek = new Slider { Minimum = 0, Maximum = 1, Value = 0, VerticalAlignment = VerticalAlignment.Center, IsTabStop = false, ThumbToolTipValueConverter = new TimeConverter() };
        _seek.ValueChanged += (_, e) =>
        {
            if (_settingSeek || _player is null) return;
            UpdateTimeText((long)e.NewValue);
            if (!_dragging) _player.Time = (long)e.NewValue; // teclado o clic en la barra
        };
        // El arrastre solo busca al soltar.
        _seek.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => _dragging = true), true);
        PointerEventHandler release = (_, _) =>
        {
            if (!_dragging) return;
            _dragging = false;
            if (_player is not null && _seek is not null) _player.Time = (long)_seek.Value;
            Refocus();
        };
        _seek.AddHandler(UIElement.PointerReleasedEvent, release, true);
        _seek.AddHandler(UIElement.PointerCaptureLostEvent, release, true);

        _time = new TextBlock { Foreground = new SolidColorBrush(Colors.White), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0), Text = "0:00 / 0:00" };

        _muteButton = IconButton("");
        _muteButton.Click += (_, _) => { ToggleMute(); Refocus(); };
        _volume = new Slider { Minimum = 0, Maximum = 100, Value = 100, Width = 90, VerticalAlignment = VerticalAlignment.Center, IsTabStop = false };
        _volume.ValueChanged += (_, e) =>
        {
            if (_player is { } p)
            {
                p.Volume = (int)e.NewValue;
                if (e.NewValue > 0 && p.Mute) p.Mute = false;
            }
            UpdateVolumeIcon();
        };

        _audioMenu = new MenuFlyout();
        _audioMenu.Opening += (_, _) => { _menuOpen = true; FillTrackMenu(_audioMenu, audio: true); };
        _audioMenu.Closed += (_, _) => _menuOpen = false;
        _subtitleMenu = new MenuFlyout();
        _subtitleMenu.Opening += (_, _) => { _menuOpen = true; FillTrackMenu(_subtitleMenu, audio: false); };
        _subtitleMenu.Closed += (_, _) => _menuOpen = false;
        var audioButton = IconButton("", "Audio");
        audioButton.Flyout = _audioMenu;
        var subtitleButton = IconButton("", "Subtítulos");
        subtitleButton.Flyout = _subtitleMenu;
        var fullButton = IconButton("", "Pantalla completa (F)");
        fullButton.Click += (_, _) => { FullScreen.Toggle(); Refocus(); };

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(_muteButton);
        right.Children.Add(_volume);
        right.Children.Add(audioButton);
        right.Children.Add(subtitleButton);
        right.Children.Add(fullButton);

        var bar = new Grid
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Padding = new Thickness(16, 28, 16, 12),
            Background = new LinearGradientBrush
            {
                StartPoint = new global::Windows.Foundation.Point(0, 0),
                EndPoint = new global::Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = ColorHelper.FromArgb(0, 0, 0, 0), Offset = 0 },
                    new GradientStop { Color = ColorHelper.FromArgb(200, 0, 0, 0), Offset = 1 },
                },
            },
        };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_seek, 1);
        Grid.SetColumn(_time, 2);
        Grid.SetColumn(right, 3);
        bar.Children.Add(_playButton);
        bar.Children.Add(_seek);
        bar.Children.Add(_time);
        bar.Children.Add(right);
        bar.Tapped += (_, e) => e.Handled = true; // un clic en los controles no pausa el video
        bar.DoubleTapped += (_, e) => e.Handled = true;
        bar.PointerEntered += (_, _) => _pointerOverBar = true;
        bar.PointerExited += (_, _) => _pointerOverBar = false;
        bar.PointerMoved += (_, _) => Activity();
        _bar = bar;
        return bar;
    }

    private static Button IconButton(string glyph, string? tooltip = null)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 18, Foreground = new SolidColorBrush(Colors.White) },
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Width = 44,
            Height = 40,
            IsTabStop = false,
        };
        if (tooltip is not null) ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    /// <summary>Rellena el menú con las pistas actuales de VLC y marca la activa.</summary>
    private void FillTrackMenu(MenuFlyout menu, bool audio)
    {
        menu.Items.Clear();
        if (_player is not { } player) return;
        var tracks = audio ? player.AudioTrackDescription : player.SpuDescription;
        var current = audio ? player.AudioTrack : player.Spu;
        if (tracks is null || tracks.Length == 0)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = audio ? "Sin pistas de audio" : "Sin subtítulos", IsEnabled = false });
            return;
        }
        foreach (var track in tracks)
        {
            var id = track.Id;
            var item = new ToggleMenuFlyoutItem { Text = track.Name ?? $"#{id}", IsChecked = id == current };
            item.Click += (_, _) =>
            {
                if (_player is null) return;
                if (audio) _player.SetAudioTrack(id);
                else _player.SetSpu(id);
                Refocus();
            };
            menu.Items.Add(item);
        }
    }

    private void TogglePlay()
    {
        if (_player is not { } player) return;
        if (player.IsPlaying) player.Pause();
        else player.Play();
    }

    private void ToggleMute()
    {
        if (_player is { } p) p.Mute = !p.Mute;
        UpdateVolumeIcon();
    }

    private void UpdatePlayIcon()
    {
        if (_playButton?.Content is FontIcon icon)
            icon.Glyph = _player?.IsPlaying == true ? "" : ""; // pausa / play
    }

    private void UpdateVolumeIcon()
    {
        if (_muteButton?.Content is not FontIcon icon) return;
        icon.Glyph = _player?.Mute == true || _volume?.Value == 0 ? "" : "";
    }

    private void SetBuffering(bool on)
    {
        if (_ring is null) return;
        _ring.IsActive = on;
        _ring.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnLength(long length)
    {
        if (_seek is null) return;
        _settingSeek = true;
        _seek.Maximum = Math.Max(1, length);
        _settingSeek = false;
        UpdateTimeText(_player?.Time ?? 0);
    }

    private void OnTime(long time)
    {
        if (_seek is null || _dragging) return;
        _settingSeek = true;
        _seek.Value = Math.Min(time, _seek.Maximum);
        _settingSeek = false;
        UpdateTimeText(time);
    }

    private void UpdateTimeText(long time)
    {
        if (_time is not null) _time.Text = $"{Format(time)} / {Format(Math.Max(0, _player?.Length ?? 0))}";
    }

    internal static string Format(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private sealed class TimeConverter : Microsoft.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) => Format((long)(double)value);

        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
    }

    // ── Barra visible / teclado ──────────────────────────────────────────────────────────────────────────────────

    private void Refocus() => PlatformView?.Focus(FocusState.Programmatic);

    /// <summary>Movimiento del mouse o tecla: muestra los controles y reinicia la cuenta para ocultarlos.</summary>
    private void Activity()
    {
        ShowBar();
        _hideTimer?.Stop();
        _hideTimer?.Start();
    }

    private void ShowBar()
    {
        if (_bar is null) return;
        _bar.Visibility = Visibility.Visible;
        if (_barVisible) return;
        _barVisible = true;
        VirtualView?.RaiseControllerVisibility(true);
    }

    private void HideBarIfPlaying()
    {
        if (_bar is null || !_barVisible || _player?.IsPlaying != true) return;
        if (_pointerOverBar || _menuOpen || _dragging) { _hideTimer?.Start(); return; }
        _bar.Visibility = Visibility.Collapsed;
        _barVisible = false;
        VirtualView?.RaiseControllerVisibility(false);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var onSlider = e.OriginalSource is Slider or Microsoft.UI.Xaml.Controls.Primitives.Thumb;
        switch (e.Key)
        {
            case VirtualKey.Space when e.OriginalSource is not Button:
                TogglePlay();
                break;
            case VirtualKey.Left when !onSlider:
                SeekBy(-SeekStep);
                break;
            case VirtualKey.Right when !onSlider:
                SeekBy(SeekStep);
                break;
            case VirtualKey.Up when !onSlider:
                ChangeVolume(+5);
                break;
            case VirtualKey.Down when !onSlider:
                ChangeVolume(-5);
                break;
            case VirtualKey.F:
            case VirtualKey.F11:
                FullScreen.Toggle();
                break;
            case VirtualKey.M:
                ToggleMute();
                break;
            case VirtualKey.Escape:
                // Esc sale de pantalla completa; estando en ventana vuelve a la pantalla anterior.
                if (FullScreen.IsActive) FullScreen.Exit();
                else _ = Shell.Current?.GoToAsync("..");
                break;
            default:
                return;
        }
        e.Handled = true;
        Activity();
    }

    private void SeekBy(TimeSpan delta)
    {
        if (_player is not { } p || p.Length <= 0) return;
        p.Time = Math.Clamp(p.Time + (long)delta.TotalMilliseconds, 0, p.Length);
    }

    private void ChangeVolume(int delta)
    {
        if (_volume is null) return;
        _volume.Value = Math.Clamp(_volume.Value + delta, 0, 100);
    }
}
