using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Parche.App.Localization;
using Parche.App.Plugins;
using Parche.App.Services;
using Parche.Core.Addons;
using Parche.Core.Models;

namespace Parche.App.ViewModels;

/// <summary>Lista ordenada de fuentes de un título o episodio, con filtro por idioma.</summary>
public partial class StreamPickerViewModel : BaseViewModel, IQueryAttributable
{
    private readonly EngineProvider _engines;
    private MediaDetail? _detail;
    private Episode? _episode;
    private List<StreamRow> _all = new();
    private IReadOnlyList<SubtitleTrack> _subtitles = Array.Empty<SubtitleTrack>();
    private string _filter = "";
    private int _version;

    private readonly StreamResolvers _resolvers;

    public StreamPickerViewModel(EngineProvider engines, StreamResolvers resolvers)
    {
        _engines = engines;
        _resolvers = resolvers;
        Filters.Add(new ChipOption(LocalizationManager.T("Common_All"), "") { IsSelected = true });
        Filters.Add(new ChipOption(LocalizationManager.T("Streams_LangLatino"), "es-419"));
        Filters.Add(new ChipOption(LocalizationManager.T("Streams_LangEs"), "es"));
        Filters.Add(new ChipOption(LocalizationManager.T("Streams_LangEn"), "en"));
        LocalizationManager.Instance.PropertyChanged += (_, _) => RelabelFilters();
    }

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _subtitle = "";

    /// <summary>Hay fuentes (aunque el filtro de idioma las oculte todas): se muestran el botón y los filtros.</summary>
    [ObservableProperty]
    private bool _hasStreams;

    public ObservableCollection<ChipOption> Filters { get; } = new();
    public ObservableCollection<StreamRow> Rows { get; } = new();

    /// <summary>Terminó la carga y no hay ninguna fuente (ni con filtro): se muestra la explicación.</summary>
    public bool IsEmpty => !IsBusy && !HasError && !NoAddons && _all.Count == 0 && _loadedOnce;
    public bool NoMatch => !IsBusy && _all.Count > 0 && Rows.Count == 0;

    /// <summary>No hay ningún addon de fuentes configurado: se invita a agregar uno en Ajustes (no es un error).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _noAddons;

    [RelayCommand]
    private Task OpenSettingsAsync() => Shell.Current.GoToAsync("//settings");

    private bool _loadedOnce;


    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (!query.TryGetValue("detail", out var d) || d is not MediaDetail detail) return;
        var episode = query.TryGetValue("episode", out var e) ? e as Episode : null;
        if (detail == _detail && episode == _episode) return; // Shell reaplica la consulta al volver

        _detail = detail;
        _episode = episode;
        Title = detail.Title;
        Subtitle = episode is null ? "" : LocalizationManager.T("Detail_EpisodeLabel", episode.Season, episode.Number, episode.Title);
        _ = LoadAsync();
    }

    private string PlayTitle => _episode is null || _detail is null
        ? _detail?.Title ?? ""
        : $"{_detail.Title} · {Subtitle}";

    [RelayCommand]
    private Task LoadAsync()
    {
        var version = ++_version;
        _loadedOnce = false;
        _all = new();
        Rows.Clear();
        return RunAsync(async () =>
        {
            try
            {
                var engine = _engines.Engine;
                NoAddons = !engine.HasStreamAddons;
                if (NoAddons) return; // sin addons no hay nada que consultar: se muestra cómo agregar uno
                var streamsTask = engine.GetStreamsAsync(_detail!, _episode);
                var subsTask = engine.GetSubtitlesAsync(_detail!, _episode);
                var streams = await streamsTask;
                try { _subtitles = await subsTask; } catch (Exception) { _subtitles = Array.Empty<SubtitleTrack>(); }
                if (version != _version) return;
                _all = streams.Select(s => new StreamRow(s)).ToList();
                // La mejor fuente empieza a conectar mientras el usuario mira la lista.
                if (streams.FirstOrDefault() is { NeedsResolver: true } best) _ = _resolvers.PreloadAsync(best);
            }
            finally
            {
                _loadedOnce = true;
                ApplyFilter();
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(NoMatch));
            }
        });
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(IsBusy) or nameof(Error))
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(NoMatch));
        }
    }

    private void RelabelFilters()
    {
        // Los chips guardan la etiqueta al crearse: se recrean con el idioma nuevo.
        var selected = _filter;
        Filters.Clear();
        Filters.Add(new ChipOption(LocalizationManager.T("Common_All"), ""));
        Filters.Add(new ChipOption(LocalizationManager.T("Streams_LangLatino"), "es-419"));
        Filters.Add(new ChipOption(LocalizationManager.T("Streams_LangEs"), "es"));
        Filters.Add(new ChipOption(LocalizationManager.T("Streams_LangEn"), "en"));
        foreach (var f in Filters) f.IsSelected = (string?)f.Value == selected;
    }

    [RelayCommand]
    private void SetFilter(ChipOption option)
    {
        _filter = option.Value as string ?? "";
        foreach (var f in Filters) f.IsSelected = ReferenceEquals(f, option);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        Rows.Clear();
        foreach (var r in _all.Where(Matches)) Rows.Add(r);
        HasStreams = _all.Count > 0;
        OnPropertyChanged(nameof(NoMatch));
    }

    // Solo coincidencias explícitas: "Multi" (DUAL/MULTI sin detalle) suele ser inglés + japonés u otros, no el
    // idioma elegido, así que no aparece bajo un filtro de idioma (sí en "Todos").
    private bool Matches(StreamRow r) => _filter.Length == 0 || r.Candidate.Languages.Contains(_filter);

    [RelayCommand]
    private Task PlayAsync(StreamRow row) => _detail is null
        ? Task.CompletedTask
        : Nav.ToPlayerAsync(row.Candidate, _subtitles, PlayTitle, _detail.ToItem(), _episode,
            Rows.SkipWhile(r => r != row).Skip(1).Select(r => r.Candidate).ToList());

    /// <summary>Reproduce la primera fuente de la lista (la mejor según el ranking).</summary>
    [RelayCommand]
    private Task PlayBestAsync() => Rows.FirstOrDefault() is { } best ? PlayAsync(best) : Task.CompletedTask;
}
