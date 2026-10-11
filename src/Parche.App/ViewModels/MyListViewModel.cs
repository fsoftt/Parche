using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Parche.App.Localization;
using Parche.App.Services;
using Parche.Core.Library;
using Parche.Core.Models;

namespace Parche.App.ViewModels;

public partial class MyListViewModel : BaseViewModel
{
    private readonly MyListService _myList;
    private readonly DownloadService _downloads;

    public MyListViewModel(MyListService myList, DownloadService downloads)
    {
        _myList = myList;
        _downloads = downloads;
        foreach (var d in downloads.Items) Downloads.Add(new DownloadRow(d, downloads));
        HasDownloads = Downloads.Count > 0;
        myList.Items.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasListItems)); OnPropertyChanged(nameof(IsEmpty)); };
        downloads.Updated += OnDownloadUpdated;
        downloads.Removed += OnDownloadRemoved;
        LocalizationManager.Instance.PropertyChanged += (_, _) => RefreshRows();
    }

    public ObservableCollection<MediaItem> Items => _myList.Items;

    public ObservableCollection<DownloadRow> Downloads { get; } = new();

    public bool HasListItems => _myList.Items.Count > 0;

    /// <summary>Ni lista ni descargas: se muestra la explicación.</summary>
    public bool IsEmpty => !HasDownloads && _myList.Items.Count == 0;

    /// <summary>Hay descargas: se muestra la sección "Descargas" encima de la lista.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasDownloads;

    [RelayCommand]
    private Task OpenAsync(MediaItem item) => Nav.ToDetailAsync(item);

    /// <summary>Descarga terminada: se ve sin conexión. Las demás alternan pausa y reanudación.</summary>
    [RelayCommand]
    private async Task ActivateDownloadAsync(DownloadRow row)
    {
        if (row.Item.State != DownloadState.Completed)
        {
            ToggleDownload(row);
            return;
        }
        if (_downloads.FindPlayable(row.Item.Item, row.Item.Season, row.Item.Episode) is { } playable)
            await Nav.ToOfflinePlayerAsync(playable);
        else
            await Shell.Current.DisplayAlert(LocalizationManager.T("Downloads_Title"), LocalizationManager.T("Downloads_FileMissing"), LocalizationManager.T("Common_Close"));
    }

    [RelayCommand]
    private void ToggleDownload(DownloadRow row)
    {
        switch (row.Item.State)
        {
            case DownloadState.Queued or DownloadState.Downloading: _downloads.Pause(row.Id); break;
            case DownloadState.Paused or DownloadState.Failed: _downloads.Resume(row.Id); break;
        }
    }

    [RelayCommand]
    private async Task DeleteDownloadAsync(DownloadRow row)
    {
        var name = row.HasSubtitle ? $"{row.Title} · {row.Subtitle}" : row.Title;
        var ok = await Shell.Current.DisplayAlert(LocalizationManager.T("Downloads_Delete"),
            LocalizationManager.T("Downloads_DeleteConfirm", name),
            LocalizationManager.T("Downloads_Delete"), LocalizationManager.T("Common_Cancel"));
        if (ok) await _downloads.RemoveAsync(row.Id);
    }

    private void OnDownloadUpdated(object? sender, DownloadItem item)
    {
        var row = Downloads.FirstOrDefault(r => r.Id == item.Id);
        if (row is null)
        {
            Downloads.Add(new DownloadRow(item, _downloads));
            HasDownloads = true;
        }
        else row.Apply(item);
    }

    private void OnDownloadRemoved(object? sender, string id)
    {
        if (Downloads.FirstOrDefault(r => r.Id == id) is { } row) Downloads.Remove(row);
        HasDownloads = Downloads.Count > 0;
    }

    private void RefreshRows()
    {
        foreach (var r in Downloads) r.Apply(r.Item);
    }
}
