using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parche.Core.Addons;

/// <summary>Una opción de reproducción devuelta por un addon de fuentes (enlace directo o referencia que un resolvedor convierte en enlace).</summary>
public sealed record StreamCandidate
{
    /// <summary>Addon de origen (host de su URL base).</summary>
    public string Addon { get; init; } = "";
    public string Name { get; init; } = "";
    public string Title { get; init; } = "";
    /// <summary>"2160p", "1080p", "720p", "480p" o "SD".</summary>
    public string Quality { get; init; } = "SD";
    public long? SizeBytes { get; init; }
    /// <summary>Disponibilidad que informa el addon (mayor = más disponible); null si no la indica.</summary>
    public int? Popularity { get; init; }
    /// <summary>Sitio de origen que informa el addon (p. ej. "SitioA"); null si no lo indica.</summary>
    public string? Provider { get; init; }
    /// <summary>Códigos de idioma: "es", "es-419", "en", "pt", … o "multi" para DUAL/MULTI sin detalle.</summary>
    [JsonConverter(typeof(ReadOnlySetJsonConverter))]
    public IReadOnlySet<string> Languages { get; init; } = new HashSet<string> { "en" };
    /// <summary>Identificador de contenido del protocolo de addons (<c>infoHash</c>); sin <see cref="Url"/> necesita un <c>IStreamResolver</c>.</summary>
    public string? InfoHash { get; init; }
    public int? FileIdx { get; init; }
    /// <summary>Enlace HTTP(S) directo.</summary>
    public string? Url { get; init; }
    /// <summary>Pistas de conexión que informa el addon (campo <c>sources</c> del protocolo, entradas "tracker:…"); las usa el resolvedor.</summary>
    public IReadOnlyList<string> ConnectionHints { get; init; } = Array.Empty<string>();
    public string? FileName { get; init; }
    /// <summary>El addon entrega un enlace directo (no una referencia por resolver).</summary>
    public bool IsDirect { get; init; }
    /// <summary>El addon indica que está lista para reproducir al instante.</summary>
    public bool IsInstant { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public string? BingeGroup { get; init; }

    /// <summary>Tiene identificador pero no enlace: solo se puede reproducir si un resolvedor lo convierte en enlace.</summary>
    public bool NeedsResolver => !string.IsNullOrEmpty(InfoHash) && string.IsNullOrEmpty(Url);

    /// <summary>Clave para deduplicar entre addons.</summary>
    public string DedupKey => !string.IsNullOrEmpty(InfoHash)
        ? "h:" + InfoHash.ToLowerInvariant() + ":" + (FileIdx?.ToString() ?? "")
        : "u:" + Url;

}

/// <summary>Permite leer de JSON un <see cref="IReadOnlySet{T}"/> de cadenas (System.Text.Json no sabe crearlo).</summary>
internal sealed class ReadOnlySetJsonConverter : JsonConverter<IReadOnlySet<string>>
{
    public override IReadOnlySet<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<HashSet<string>>(ref reader, options) ?? new HashSet<string>();

    public override void Write(Utf8JsonWriter writer, IReadOnlySet<string> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.ToArray(), options);
}
