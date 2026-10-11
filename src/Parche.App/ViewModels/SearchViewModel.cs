using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Parche.App.Services;
using Parche.Core.Models;

namespace Parche.App.ViewModels;

public partial class SearchViewModel : BaseViewModel
{
    private readonly EngineProvider _engines;
    private CancellationTokenSource? _debounce;

    public SearchViewModel(EngineProvider engines)
    {
        _engines = engines;
        _engines.Changed += (_, _) => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (Query.Trim().Length >= 2) SearchCommand.Execute(null);
        });
    }

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private bool _noResults;

    public ObservableCollection<MediaItem> Results { get; } = new();

    partial void OnQueryChanged(string value)
    {
        // Búsqueda mientras se escribe, con espera para no consultar en cada tecla.
        _debounce?.Cancel();
        _debounce = new CancellationTokenSource();
        var ct = _debounce.Token;
        _ = Task.Delay(500, ct).ContinueWith(t =>
        {
            if (!t.IsCanceled) MainThread.BeginInvokeOnMainThread(() => SearchCommand.Execute(null));
        }, TaskScheduler.Default);
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        var q = Query.Trim();
        if (q.Length < 2)
        {
            Results.Clear();
            NoResults = false;
            return;
        }

        // Si hay una búsqueda en curso, espera a que termine y vuelve a comprobar el texto.
        while (IsBusy) await Task.Delay(100);
        if (q != Query.Trim()) return;

        await RunAsync(async () =>
        {
            var items = await _engines.Engine.SearchAsync(q);
            if (q != Query.Trim()) return; // llegó tarde, el usuario siguió escribiendo
            Results.Clear();
            foreach (var i in items) Results.Add(i);
            NoResults = Results.Count == 0;
        });
    }

    [RelayCommand]
    private Task OpenAsync(MediaItem item) => Nav.ToDetailAsync(item);
}
