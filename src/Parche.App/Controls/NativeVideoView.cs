using Parche.Core.Models;

namespace Parche.App.Controls;

/// <summary>Reproductor nativo (ExoPlayer en Android) con soporte de cabeceras HTTP y control remoto de TV.</summary>
public class NativeVideoView : View
{
    public static readonly BindableProperty StreamProperty =
        BindableProperty.Create(nameof(Stream), typeof(PlaybackRequest), typeof(NativeVideoView));

    public static readonly BindableProperty TitleProperty =
        BindableProperty.Create(nameof(Title), typeof(string), typeof(NativeVideoView));

    public PlaybackRequest? Stream
    {
        get => (PlaybackRequest?)GetValue(StreamProperty);
        set => SetValue(StreamProperty, value);
    }

    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>El reproductor no pudo abrir el stream (403, formato no soportado...).</summary>
    public event EventHandler<PlaybackFailure>? PlaybackFailed;

    /// <summary>Los controles nativos aparecieron (true) o se ocultaron (false): sirve para mostrar/ocultar la barra de opciones.</summary>
    public event EventHandler<bool>? ControllerVisibilityChanged;

    public event EventHandler? PlaybackEnded;

    /// <summary>Avance de la reproducción (cada pocos segundos, al pausar y al cerrar).</summary>
    public event EventHandler<PlaybackProgress>? ProgressChanged;

    internal void RaiseFailed(PlaybackFailure failure) => PlaybackFailed?.Invoke(this, failure);

    internal void RaiseControllerVisibility(bool visible) => ControllerVisibilityChanged?.Invoke(this, visible);

    internal void RaiseEnded() => PlaybackEnded?.Invoke(this, EventArgs.Empty);

    internal void RaiseProgress(PlaybackProgress progress) => ProgressChanged?.Invoke(this, progress);
}

/// <summary>Detalle de un error del reproductor nativo.</summary>
/// <param name="Message">Texto legible con la causa.</param>
/// <param name="ErrorCodeName">Nombre del código de error de ExoPlayer (p.ej. ERROR_CODE_IO_BAD_HTTP_STATUS).</param>
/// <param name="HttpStatus">Código HTTP cuando el servidor rechazó la petición (403, 404...).</param>
public sealed record PlaybackFailure(string Message, string? ErrorCodeName = null, int? HttpStatus = null);

/// <summary>Posición y duración actuales del video, en milisegundos (duración 0 si aún no se conoce).</summary>
public sealed record PlaybackProgress(long PositionMs, long DurationMs);
