using CommunityToolkit.Mvvm.ComponentModel;
using Parche.App.Localization;
using Parche.App.Services;
using Parche.Core.Library;

namespace Parche.App.ViewModels;

/// <summary>Fila de la lista de descargas: se actualiza en sitio para no perder el foco del D-pad con cada avance.</summary>
public sealed partial class DownloadRow : ObservableObject
{
    private readonly DownloadService _service;

    public DownloadRow(DownloadItem item, DownloadService service)
    {
        _service = service;
        Item = item;
        Apply(item);
    }

    public DownloadItem Item { get; private set; }
    public string Id => Item.Id;
    public string? Image => Item.Item.Wide;
    public string Title => Item.Item.Title;

    /// <summary>"T1:E2 · Título del episodio" en series; vacío en películas.</summary>
    public string Subtitle => Item.IsEpisode
        ? LocalizationManager.T("Detail_EpisodeLabel", Item.Season!, Item.Episode!, Item.EpisodeTitle ?? "")
        : "";

    public bool HasSubtitle => Subtitle.Length > 0;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private double _fraction;

    /// <summary>La barra de progreso se ve mientras no esté terminada.</summary>
    [ObservableProperty]
    private bool _showProgress;

    /// <summary>Texto del botón Pausar / Reanudar / Reintentar; vacío (oculto) en descargas terminadas.</summary>
    [ObservableProperty]
    private string _toggleText = "";

    [ObservableProperty]
    private bool _canToggle;

    public bool IsCompleted => Item.State == DownloadState.Completed;

    /// <summary>Recalcula los textos con el estado nuevo (o con otra red permitida).</summary>
    public void Apply(DownloadItem item)
    {
        Item = item;
        Fraction = item.Fraction;
        ShowProgress = item.State != DownloadState.Completed;
        CanToggle = item.State != DownloadState.Completed;
        ToggleText = item.State switch
        {
            DownloadState.Queued or DownloadState.Downloading => LocalizationManager.T("Downloads_Pause"),
            DownloadState.Paused => LocalizationManager.T("Downloads_Resume"),
            DownloadState.Failed => LocalizationManager.T("Common_Retry"),
            _ => "",
        };
        Status = BuildStatus(item);
        OnPropertyChanged(nameof(Item));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(IsCompleted));
    }

    private string BuildStatus(DownloadItem d)
    {
        var sizes = d.BytesTotal > 0
            ? LocalizationManager.T("Downloads_Of", Format.Size(Math.Max(d.BytesDone, 1)), Format.Size(d.BytesTotal))
            : "";
        switch (d.State)
        {
            case DownloadState.Queued:
                return _service.WaitingForNetwork
                    ? LocalizationManager.T(_service.WifiOnly ? "Downloads_WaitingWifi" : "Downloads_WaitingNetwork")
                    : LocalizationManager.T("Downloads_Queued");
            case DownloadState.Downloading:
                if (d.BytesTotal <= 0) return LocalizationManager.T("Downloads_Searching");
                var speed = _service.SpeedOf(d.Id) is > 0 and var s ? LocalizationManager.T("Player_Speed", s / 1048576.0) : "";
                return Format.Join(LocalizationManager.T("Downloads_Downloading"), $"{d.Fraction * 100:0}%", sizes, speed);
            case DownloadState.Paused:
                return Format.Join(LocalizationManager.T("Downloads_Paused"), d.BytesTotal > 0 ? $"{d.Fraction * 100:0}%" : "");
            case DownloadState.Completed:
                return Format.Join(LocalizationManager.T("Downloads_Completed"), Format.Size(d.BytesTotal));
            default:
                return string.IsNullOrEmpty(d.Error) ? LocalizationManager.T("Downloads_Failed") : d.Error;
        }
    }
}
