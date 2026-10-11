using System.Text.Json;
using System.Text.Json.Serialization;
using Parche.Core.Addons;

namespace Parche.Core.Settings;

[JsonConverter(typeof(JsonStringEnumConverter<CatalogProviderKind>))]
public enum CatalogProviderKind
{
    /// <summary>TMDB si hay clave; si no, Cinemeta.</summary>
    Auto,
    Tmdb,
    Cinemeta,
}

/// <summary>Ajustes de la app; serializables con System.Text.Json.</summary>
public sealed class AppSettings
{
    public const string DefaultLanguage = "es-ES";

    public CatalogProviderKind CatalogProvider { get; set; } = CatalogProviderKind.Auto;
    public string? TmdbApiKey { get; set; }
    /// <summary>Idioma de los metadatos de TMDB: es-ES, es-MX, en-US, pt-BR…</summary>
    public string AppLanguage { get; set; } = DefaultLanguage;

    /// <summary>
    /// URLs base de los addons de fuentes que agregó el usuario (vacío por defecto). Los plugins pueden aportar más
    /// mediante <see cref="IStreamSourceProvider"/>; el motor une ambas listas.
    /// </summary>
    public List<string> StreamAddons { get; set; } = new();

    public List<string> PreferredLanguages { get; set; } = new() { "es-419", "es", "en" };
    /// <summary>Tope de calidad preferido ("2160p", "1080p", "720p", "480p"); vacío = sin tope.</summary>
    public string PreferredMaxQuality { get; set; } = "1080p";

    /// <summary>URLs base de los addons de subtítulos; se consultan todos y se unen los resultados.</summary>
    public List<string> SubtitleAddons { get; set; } = new() { SubtitlesClient.DefaultBase };
    /// <summary>Idiomas de subtítulos (ISO 639-2 de 3 letras, en orden de preferencia).</summary>
    public List<string> SubtitleLanguages { get; set; } = new() { "spa", "eng" };

    /// <summary>Las descargas para ver sin conexión solo avanzan con WiFi (o red cableada).</summary>
    public bool DownloadOnlyOnWifi { get; set; } = true;

    /// <summary>
    /// Ajustes de los plugins, por clave. Cada plugin lee y escribe su propio JSON con
    /// <see cref="GetPlugin{T}"/> y <see cref="SetPlugin{T}"/>.
    /// </summary>
    public Dictionary<string, JsonElement> Plugins { get; set; } = new();

    /// <summary>
    /// Propiedades desconocidas del JSON (de versiones anteriores o de plugins): se conservan al guardar y los plugins
    /// pueden migrarlas con <see cref="GetExtra{T}"/>.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public bool HasTmdbKey => !string.IsNullOrWhiteSpace(TmdbApiKey);

    private static readonly string[] Qualities = { "2160p", "1080p", "720p", "480p" };

    /// <summary>Lista de problemas (vacía = válido); mensajes en español para mostrar en ajustes.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (CatalogProvider == CatalogProviderKind.Tmdb && !HasTmdbKey) errors.Add("Falta la clave de TMDB.");
        if (HasTmdbKey && TmdbApiKey!.Any(char.IsWhiteSpace)) errors.Add("La clave de TMDB no debe contener espacios.");
        if (!Qualities.Contains(PreferredMaxQuality ?? "", StringComparer.OrdinalIgnoreCase) && !string.IsNullOrEmpty(PreferredMaxQuality))
            errors.Add("Calidad máxima inválida (2160p, 1080p, 720p o 480p).");
        errors.AddRange(StreamAddons.Where(a => !IsHttpUrl(AddonClient.NormalizeBase(a))).Select(a => "URL de addon inválida: " + a));
        errors.AddRange(SubtitleAddons.Where(a => !IsHttpUrl(AddonClient.NormalizeBase(a))).Select(a => "URL de addon de subtítulos inválida: " + a));
        if (PreferredLanguages.Count == 0) errors.Add("Elige al menos un idioma preferido.");
        return errors;
    }

    public bool IsValid => Validate().Count == 0;

    private static bool IsHttpUrl(string? s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    // ---------- serialización ----------

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Lee ajustes guardados; si el texto está vacío o corrupto devuelve los valores por defecto.</summary>
    public static AppSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AppSettings();
        try { return JsonSerializer.Deserialize<AppSettings>(json, Json)?.Normalize() ?? new AppSettings(); }
        catch (JsonException) { return new AppSettings(); }
    }

    /// <summary>Repone listas/objetos nulos (JSON antiguo o editado a mano) y migra propiedades antiguas.</summary>
    public AppSettings Normalize()
    {
        StreamAddons ??= new();
        PreferredLanguages ??= new();
        SubtitleLanguages ??= new();
        SubtitleAddons ??= new() { SubtitlesClient.DefaultBase }; // lista vacía a propósito = sin subtítulos externos
        Plugins ??= new();
        if (string.IsNullOrWhiteSpace(AppLanguage)) AppLanguage = DefaultLanguage;
        MigrateSubtitleAddon();
        return this;
    }

    private const string LegacySubtitlesKey = "SubtitlesAddonUrl";

    /// <summary>Ajustes antiguos con un solo addon (<c>SubtitlesAddonUrl</c>): pasa a la lista si el usuario lo había cambiado.</summary>
    private void MigrateSubtitleAddon()
    {
        if (Extra is null || !Extra.Remove(LegacySubtitlesKey, out var legacy)) return;
        if (Extra.Count == 0) Extra = null;
        var url = legacy.ValueKind == JsonValueKind.String ? legacy.GetString() : null;
        if (!string.IsNullOrWhiteSpace(url) && SubtitleAddons.Count == 1 && SubtitleAddons[0] == SubtitlesClient.DefaultBase)
            SubtitleAddons = new() { url.Trim() };
    }

    // ---------- plugins ----------

    private static readonly JsonSerializerOptions PluginJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Ajustes guardados de un plugin; null si no hay o no se pueden leer.</summary>
    public T? GetPlugin<T>(string key) where T : class
    {
        if (Plugins is null || !Plugins.TryGetValue(key, out var json)) return null;
        return Read<T>(json);
    }

    /// <summary>Guarda los ajustes de un plugin bajo su clave.</summary>
    public void SetPlugin<T>(string key, T value) where T : class =>
        (Plugins ??= new())[key] = JsonSerializer.SerializeToElement(value, PluginJson);

    /// <summary>Lee una propiedad desconocida del JSON (p. ej. un objeto de una versión anterior) para migrarla; null si no existe o no se puede leer.</summary>
    public T? GetExtra<T>(string name) where T : class =>
        Extra?.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)) is { Key: not null } found ? Read<T>(found.Value) : null;

    /// <summary>Descarta una propiedad desconocida ya migrada, para que no vuelva a aplicarse.</summary>
    public void RemoveExtra(string name)
    {
        if (Extra is null) return;
        foreach (var key in Extra.Keys.Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToList()) Extra.Remove(key);
        if (Extra.Count == 0) Extra = null;
    }

    private static T? Read<T>(JsonElement json) where T : class
    {
        if (json.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        try { return json.Deserialize<T>(PluginJson); }
        catch (JsonException) { return null; }
    }
}
