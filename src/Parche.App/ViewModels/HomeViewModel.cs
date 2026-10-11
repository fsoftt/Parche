using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Parche.App.Localization;
using Parche.App.Services;
using Parche.Core.Models;

namespace Parche.App.ViewModels;

public partial class HomeViewModel : BaseViewModel
{
    private readonly EngineProvider _engines;
    private readonly MyListService _myList;
    private readonly WatchProgressService _progress;
    private readonly DownloadService _downloads;
    private bool _loaded;
    private bool _rebuildQueued;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroInMyList))]
    private MediaItem? _hero;

    /// <summary>Título enfocado con el control remoto (TV): se muestra en el banner superior.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewMeta))]
    private MediaItem? _preview;

    public ObservableCollection<MediaSection> Sections { get; } = new();

    /// <summary>Fila "Continuar viendo" (vacía si no hay nada a medio ver).</summary>
    public ObservableCollection<ContinueRow> Continue { get; } = new();

    public bool HasContinue => Continue.Count > 0;

    public bool HeroInMyList => Hero is not null && _myList.Contains(Hero);

    /// <summary>"Película · 2023 · ★ 8.1" del título del banner.</summary>
    public string PreviewMeta => Preview is null ? "" : Format.Join(Format.TypeName(Preview.Type), Preview.Year?.ToString(), Format.Rating(Preview.Rating));

    public HomeViewModel(EngineProvider engines, MyListService myList, WatchProgressService progress, DownloadService downloads)
    {
        _engines = engines;
        _myList = myList;
        _progress = progress;
        _downloads = downloads;
        progress.Entries.CollectionChanged += (_, _) => ScheduleRebuildContinue();
        RebuildContinue();
        _engines.Changed += (_, _) => MainThread.BeginInvokeOnMainThread(() => _ = LoadAsync(true));
        LocalizationManager.Instance.PropertyChanged += (_, _) => OnPropertyChanged(nameof(PreviewMeta));
    }

    public Task EnsureLoadedAsync() => _loaded ? Task.CompletedTask : LoadAsync(false);

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(true);

    private Task LoadAsync(bool refresh) => RunAsync(async () =>
    {
        var feed = await _engines.GetHomeAsync(refresh, CancellationToken.None);

        Hero = feed.Hero;
        Preview = feed.Hero;
        Sections.Clear();
        foreach (var s in feed.Sections.Where(s => s.Items.Count > 0)) Sections.Add(s);
        _loaded = true;

        if (Sections.Count == 0)
            Error = LocalizationManager.T("Home_NoContent");
    });

    [RelayCommand]
    private Task OpenAsync(MediaItem item) => Nav.ToDetailAsync(item);

    [RelayCommand]
    private void Highlight(MediaItem item)
    {
        if (IsTv) Preview = item;
    }

    /// <summary>Reproducir: abre el detalle y, al cargarlo, el selector de fuentes.</summary>
    [RelayCommand]
    private Task PlayHeroAsync() => Hero is null ? Task.CompletedTask : Nav.ToDetailAsync(Hero, autoPlay: true);

    /// <summary>Clear + N Add disparan N+1 avisos: se reconstruye una sola vez al terminar el lote.</summary>
    private void ScheduleRebuildContinue()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _rebuildQueued = false;
            RebuildContinue();
        });
    }

    private void RebuildContinue()
    {
        Continue.Clear();
        foreach (var e in _progress.Entries) Continue.Add(new ContinueRow(e));
        OnPropertyChanged(nameof(HasContinue));
    }

    /// <summary>Retoma con la misma fuente y en la misma posición; sin fuente guardada, abre el detalle.</summary>
    [RelayCommand]
    private Task ResumeAsync(ContinueRow row)
    {
        var e = row.Entry;
        // Si ese título está descargado, se retoma desde el archivo local (funciona sin red).
        if (_downloads.FindPlayable(e.Item, e.Season, e.Episode) is { } offline) return Nav.ToOfflinePlayerAsync(offline);
        if (e.Source is null) return Nav.ToDetailAsync(e.Item);
        Episode? episode = e.Season is { } s && e.Episode is { } n
            ? new Episode { Season = s, Number = n, Title = e.EpisodeTitle ?? "" }
            : null;
        var title = episode is null ? e.Item.Title : $"{e.Item.Title} · {row.Subtitle}";
        return Nav.ToPlayerAsync(e.Source, Array.Empty<SubtitleTrack>(), title, e.Item, episode);
    }

    /// <summary>Quita el título de "Continuar viendo" (pulsación larga en celular).</summary>
    [RelayCommand]
    private void RemoveContinue(ContinueRow row) => _progress.Remove(row.Entry.Item);

    [RelayCommand]
    private void ToggleHeroList()
    {
        if (Hero is null) return;
        _myList.Toggle(Hero);
        OnPropertyChanged(nameof(HeroInMyList));
    }
}
