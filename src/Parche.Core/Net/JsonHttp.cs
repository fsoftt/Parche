using System.Text.Json;

namespace Parche.Core.Net;

/// <summary>
/// Cliente JSON mínimo: HttpClient + <see cref="RateLimiter"/> + <see cref="ResponseCache"/> + System.Text.Json.
/// Cada servicio (TMDB, Cinemeta, addons) usa su propia <see cref="RateLimitSpec"/>.
/// </summary>
public sealed class JsonHttp(HttpClient http, RateLimiter? limiter = null, ResponseCache? cache = null)
{
    public const string UserAgent = "Parche/1.0 (research)";

    private readonly RateLimiter _limiter = limiter ?? RateLimiter.Shared;
    private readonly ResponseCache _cache = cache ?? ResponseCache.Shared;

    public HttpClient Http => http;

    /// <summary>GET que devuelve el JSON ya parseado. Lanza <see cref="HttpRequestException"/> ante errores HTTP.</summary>
    public async Task<JsonElement> GetAsync(string url, RateLimitSpec spec, IReadOnlyDictionary<string, string>? headers = null,
        bool cache = true, int timeoutMs = 20000, CancellationToken ct = default)
    {
        var key = "json:" + url + (headers is { Count: > 0 } ? "|" + string.Join(",", headers.Keys.Order()) : "");
        if (cache && _cache.Get(key, spec.CacheSeconds) is { } hit) return Parse(hit);

        var body = await PoliteHttp.GetStringAsync(http, _limiter, spec, url, () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (headers is not null) foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
            return req;
        }, timeoutMs, ct);

        var parsed = Parse(body);
        if (cache) _cache.Set(key, body, spec.CacheSeconds);
        return parsed;
    }

    private static JsonElement Parse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }
}

/// <summary>Lectura tolerante de JSON (propiedades ausentes o de otro tipo devuelven null).</summary>
public static class JsonExt
{
    public static JsonElement? Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    public static string? Str(this JsonElement e, string name)
    {
        var v = e.Prop(name);
        return v?.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(v.Value.GetString()) ? null : v.Value.GetString(),
            JsonValueKind.Number => v.Value.GetRawText(),
            _ => null,
        };
    }

    public static int? Int(this JsonElement e, string name)
    {
        var v = e.Prop(name);
        if (v is null) return null;
        if (v.Value.ValueKind == JsonValueKind.Number && v.Value.TryGetInt32(out var i)) return i;
        if (v.Value.ValueKind == JsonValueKind.String && int.TryParse(v.Value.GetString(), out i)) return i;
        return null;
    }

    public static double? Dbl(this JsonElement e, string name)
    {
        var v = e.Prop(name);
        if (v is null) return null;
        if (v.Value.ValueKind == JsonValueKind.Number) return v.Value.GetDouble();
        if (v.Value.ValueKind == JsonValueKind.String &&
            double.TryParse(v.Value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }

    public static bool Bool(this JsonElement e, string name) => e.Prop(name)?.ValueKind == JsonValueKind.True;

    public static IEnumerable<JsonElement> Arr(this JsonElement e, string name) =>
        e.Prop(name) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();

    public static IEnumerable<string> StrArr(this JsonElement e, string name) =>
        e.Arr(name).Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(s => s.Length > 0);
}
