using System.Collections.ObjectModel;
using System.Text.Json;
using Parche.Core.Library;
using Parche.Core.Models;

namespace Parche.App.Services;

/// <summary>"Continuar viendo" guardado localmente; <see cref="Entries"/> alimenta la fila de Inicio.</summary>
public sealed class WatchProgressService
{
    private const string Key = "continue_watching_v1";
    private static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(10);

    private readonly ContinueWatching _list;
    private DateTime _lastSave;
    private readonly object _saveGate = new();
    private bool _dirty;
    private bool _rowStale;

    public ObservableCollection<WatchEntry> Entries { get; }

    public WatchProgressService()
    {
        List<WatchEntry>? saved = null;
        try { saved = JsonSerializer.Deserialize<List<WatchEntry>>(Preferences.Default.Get(Key, "[]")); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
        _list = new ContinueWatching(saved);
        Entries = new ObservableCollection<WatchEntry>(_list.Entries);
    }

    public WatchEntry? Find(MediaItem item, Episode? episode) => _list.Find(item, episode?.Season, episode?.Number);

    /// <summary>Registra el avance. Escribe a disco (fuera del hilo principal) como mucho cada 10 s; <see cref="Flush"/> guarda lo pendiente.</summary>
    public void Report(WatchEntry entry)
    {
        if (!_list.Report(entry)) return;
        _dirty = true;
        _rowStale = true;
        if (DateTime.UtcNow - _lastSave >= SaveEvery) Persist();
    }

    public void Remove(MediaItem item)
    {
        if (!_list.Remove(item)) return;
        _dirty = true;
        _rowStale = true;
        Flush();
    }

    /// <summary>Guarda lo pendiente y actualiza la fila de Inicio (al cerrar el reproductor).</summary>
    public void Flush()
    {
        Persist();
        if (!_rowStale) return;
        _rowStale = false;
        var snapshot = _list.Entries.ToList();
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Entries.Clear();
            foreach (var e in snapshot) Entries.Add(e);
        });
    }

    /// <summary>Serializa y escribe en Preferences en un hilo del pool: el guardado nunca bloquea la interfaz.</summary>
    private void Persist()
    {
        if (!_dirty) return;
        _dirty = false;
        _lastSave = DateTime.UtcNow;
        var entries = _list.Entries.ToList();
        Task.Run(() =>
        {
            lock (_saveGate)
            {
                try { Preferences.Default.Set(Key, JsonSerializer.Serialize(entries)); }
                catch (Exception) { }
            }
        });
    }
}
