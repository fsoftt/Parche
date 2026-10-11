using Parche.Core.Models;
using Parche.Core.Net;

namespace Parche.Core.Addons;

/// <summary>Cliente del protocolo de addons Stremio (recurso <c>stream</c>).</summary>
public sealed class AddonClient(JsonHttp http)
{
    /// <summary>
    /// Caché en memoria de 5 min: volver al selector de fuentes no repite la consulta.
    /// Los enlaces directos son estables; la disponibilidad que informan puede variar un poco en ese tiempo.
    /// </summary>
    public static readonly RateLimitSpec Limits = new() { MinIntervalMs = 300, MaxConcurrent = 2, CacheSeconds = 300, MaxRetries = 2 };

    /// <summary>GET {base}/stream/{type}/{id}.json. <paramref name="id"/> es "tt123" o "tt123:S:E".</summary>
    public async Task<IReadOnlyList<StreamCandidate>> GetStreamsAsync(string addonBaseUrl, string type, string id, CancellationToken ct = default)
    {
        var url = $"{NormalizeBase(addonBaseUrl)}/stream/{type}/{Uri.EscapeDataString(id).Replace("%3A", ":")}.json";
        var json = await http.GetAsync(url, Limits, cache: true, timeoutMs: 25000, ct: ct);
        return StreamParser.ParseResponse(json, AddonName(addonBaseUrl));
    }

    public static string EpisodeId(string imdbId, int season, int episode) => $"{imdbId}:{season}:{episode}";

    /// <summary>Quita "/manifest.json", barras finales y convierte stremio:// en https://.</summary>
    public static string NormalizeBase(string addonBaseUrl)
    {
        var u = addonBaseUrl.Trim();
        if (u.StartsWith("stremio://", StringComparison.OrdinalIgnoreCase)) u = "https://" + u[10..];
        u = u.TrimEnd('/');
        if (u.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase)) u = u[..^"/manifest.json".Length];
        return u.TrimEnd('/');
    }

    public static string AddonName(string addonBaseUrl) =>
        Uri.TryCreate(NormalizeBase(addonBaseUrl), UriKind.Absolute, out var u) ? u.Host : addonBaseUrl;
}
