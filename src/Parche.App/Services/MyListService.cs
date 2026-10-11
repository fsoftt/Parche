using System.Collections.ObjectModel;
using System.Text.Json;
using Parche.Core.Models;

namespace Parche.App.Services;

/// <summary>"Mi lista" guardada localmente.</summary>
public sealed class MyListService
{
    // v2: el modelo de MediaItem cambió con el nuevo núcleo; la lista antigua (otra clave) se descarta.
    private const string Key = "my_list_v2";

    private readonly object _saveGate = new();

    public ObservableCollection<MediaItem> Items { get; }

    public MyListService()
    {
        List<MediaItem> items;
        try { items = JsonSerializer.Deserialize<List<MediaItem>>(Preferences.Default.Get(Key, "[]")) ?? new(); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { items = new(); }
        Items = new ObservableCollection<MediaItem>(items.Where(IsIdentifiable));
        try { Preferences.Default.Remove("my_list"); } catch (Exception) { }
    }

    /// <summary>Mismo título: igual id de IMDb o, si no, mismo id de TMDB y tipo (un listado de TMDB puede no traer el de IMDb).</summary>
    private static bool Same(MediaItem a, MediaItem b) =>
        (a.HasImdbId && b.HasImdbId && a.Id == b.Id)
        || (a.TmdbId is { } ta && b.TmdbId is { } tb && ta == tb && a.Type == b.Type);

    private static bool IsIdentifiable(MediaItem i) => i.HasImdbId || i.TmdbId is not null;

    public bool Contains(MediaItem item) => Items.Any(i => Same(i, item));

    /// <summary>Agrega o quita. Devuelve si quedó en la lista.</summary>
    public bool Toggle(MediaItem item)
    {
        var existing = Items.FirstOrDefault(i => Same(i, item));
        if (existing is not null) Items.Remove(existing);
        else Items.Insert(0, item);
        // Se serializa y guarda fuera del hilo principal; _saveGate conserva el orden de las escrituras.
        var snapshot = Items.ToList();
        Task.Run(() =>
        {
            lock (_saveGate)
            {
                try { Preferences.Default.Set(Key, JsonSerializer.Serialize(snapshot)); }
                catch (Exception) { }
            }
        });
        return existing is null;
    }
}
