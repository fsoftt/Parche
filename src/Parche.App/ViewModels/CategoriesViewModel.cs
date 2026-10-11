using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Parche.App.Services;
using Parche.Core.Models;

namespace Parche.App.ViewModels;

/// <summary>Explorar por género: alterna Películas/Series, chips de géneros y cuadrícula con carga infinita.</summary>
public partial class CategoriesViewModel : BaseViewModel
{
    private readonly EngineProvider _engines;
    private MediaType _type = MediaType.Movie;
    private Genre? _genre;
    private int _page;          // última página cargada
    private bool _endReached;
    private bool _loadingPage;
    private int _version;       // invalida respuestas de una selección anterior
    private bool _loaded;

    public CategoriesViewModel(EngineProvider engines)
    {
        _engines = engines;
        _engines.Changed += (_, _) => MainThread.BeginInvokeOnMainThread(() =>
        {
            _loaded = false;
            _ = EnsureLoadedAsync();
        });
    }

    public ObservableCollection<ChipOption> Genres { get; } = new();
    public ObservableCollection<MediaItem> Items { get; } = new();

    public bool IsMovies => _type == MediaType.Movie;
    public bool IsSeries => _type == MediaType.Series;

    [ObservableProperty]
    private bool _isLoadingMore;

    [ObservableProperty]
    private bool _isEmpty;

    public Task EnsureLoadedAsync() => _loaded ? Task.CompletedTask : LoadGenresAsync();

    [RelayCommand]
    private Task SetTypeAsync(string type)
    {
        var next = type == "series" ? MediaType.Series : MediaType.Movie;
        if (next == _type && _loaded) return Task.CompletedTask;
        _type = next;
        OnPropertyChanged(nameof(IsMovies));
        OnPropertyChanged(nameof(IsSeries));
        return LoadGenresAsync();
    }

    private async Task LoadGenresAsync()
    {
        var version = ++_version;
        _loaded = true;
        _genre = null;
        Genres.Clear();
        ResetItems();
        Error = null;
        IsBusy = true;
        try
        {
            var genres = await _engines.Engine.GetGenresAsync(_type);
            if (version != _version) return;
            foreach (var g in genres) Genres.Add(new ChipOption(g.Name, g));
            if (Genres.Count > 0) Select(Genres[0]);
            else IsEmpty = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version != _version) return;
            Error = ex.Message;
            _loaded = false; // permite reintentar al volver a la pestaña
        }
        finally
        {
            if (version == _version) IsBusy = false;
        }
        if (version == _version && _genre is not null) await LoadPageAsync(version);
    }

    [RelayCommand]
    private async Task SelectGenreAsync(ChipOption option)
    {
        if (option.Value is not Genre || ReferenceEquals(Genres.FirstOrDefault(g => g.IsSelected), option)) return;
        var version = ++_version;
        Select(option);
        ResetItems();
        await LoadPageAsync(version);
    }

    private void Select(ChipOption option)
    {
        foreach (var g in Genres) g.IsSelected = ReferenceEquals(g, option);
        _genre = (Genre?)option.Value;
    }

    private void ResetItems()
    {
        Items.Clear();
        _page = 0;
        _endReached = false;
        _loadingPage = false;
        IsEmpty = false;
        IsLoadingMore = false;
    }

    /// <summary>Carga la siguiente página (se llama al acercarse al final de la lista).</summary>
    [RelayCommand]
    private Task LoadMoreAsync() => LoadPageAsync(_version);

    private async Task LoadPageAsync(int version)
    {
        if (_genre is null || _loadingPage || _endReached || version != _version) return;
        _loadingPage = true;
        IsLoadingMore = true;
        var genre = _genre;
        try
        {
            var items = await _engines.Engine.GetByGenreAsync(_type, genre, _page + 1);
            if (version != _version) return;
            _page++;
            // Algunos catálogos repiten títulos entre páginas.
            var fresh = items.Where(i => !Items.Any(x => x.Id == i.Id && x.TmdbId == i.TmdbId && x.Title == i.Title)).ToList();
            if (items.Count == 0) _endReached = true;
            foreach (var i in fresh) Items.Add(i);
            IsEmpty = Items.Count == 0;
            Error = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version == _version) Error = ex.Message;
        }
        finally
        {
            if (version == _version)
            {
                _loadingPage = false;
                IsLoadingMore = false;
            }
        }
    }

    [RelayCommand]
    private Task RetryAsync()
    {
        _loaded = false;
        return EnsureLoadedAsync();
    }

    [RelayCommand]
    private Task OpenAsync(MediaItem item) => Nav.ToDetailAsync(item);
}
