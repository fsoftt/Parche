using Parche.Core.Models;
using Parche.Core.Net;

namespace Parche.Core.Addons;

/// <summary>Cliente del recurso <c>subtitles</c> de Stremio (por defecto el addon de OpenSubtitles).</summary>
public sealed class SubtitlesClient(JsonHttp http)
{
    public const string DefaultBase = "https://opensubtitles-v3.strem.io";
    public const string SrtMime = "application/x-subrip";

    public static readonly RateLimitSpec Limits = new() { MinIntervalMs = 300, MaxConcurrent = 2, CacheSeconds = 600, MaxRetries = 1 };

    /// <summary>Todos los subtítulos del addon, con <see cref="SubtitleTrack.Language"/> en ISO 639-1 (es, en, pt-BR…).</summary>
    public async Task<IReadOnlyList<SubtitleTrack>> GetSubtitlesAsync(string? addonBaseUrl, string type, string id, CancellationToken ct = default)
    {
        var b = AddonClient.NormalizeBase(string.IsNullOrWhiteSpace(addonBaseUrl) ? DefaultBase : addonBaseUrl);
        var json = await http.GetAsync($"{b}/subtitles/{type}/{Uri.EscapeDataString(id).Replace("%3A", ":")}.json", Limits, timeoutMs: 25000, ct: ct);
        return Parse(json);
    }

    public static IReadOnlyList<SubtitleTrack> Parse(System.Text.Json.JsonElement json)
    {
        var result = new List<SubtitleTrack>();
        foreach (var s in json.Arr("subtitles"))
        {
            var url = s.Str("url");
            var lang = s.Str("lang");
            if (url is null || lang is null) continue;
            result.Add(new SubtitleTrack(url, LanguageCodes.ToIso1(lang), LanguageCodes.DisplayName(lang), SrtMime));
        }
        return result;
    }

    /// <summary>
    /// Filtra por idiomas deseados (códigos de 3 o 2 letras, en orden de preferencia), conserva ese orden,
    /// limita a <paramref name="perLanguage"/> por idioma y numera las etiquetas repetidas ("Español 2").
    /// </summary>
    public static IReadOnlyList<SubtitleTrack> Filter(IEnumerable<SubtitleTrack> tracks, IEnumerable<string> wanted, int perLanguage = 5)
    {
        var all = tracks.ToList();
        var result = new List<SubtitleTrack>();
        foreach (var w in wanted)
        {
            var matches = all.Where(t => t.Language is not null && LanguageCodes.SameLanguage(t.Language, w) && !result.Contains(t)).Take(perLanguage).ToList();
            for (var i = 0; i < matches.Count; i++)
                result.Add(i == 0 ? matches[i] : matches[i] with { Label = $"{matches[i].Label} {i + 1}" });
        }
        return result;
    }
}
