using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Parche.Core.Net;

namespace Parche.Core.Addons;

/// <summary>Convierte la respuesta <c>streams[]</c> de un addon Stremio en <see cref="StreamCandidate"/>.</summary>
public static partial class StreamParser
{
    public static IReadOnlyList<StreamCandidate> ParseResponse(JsonElement json, string addon) =>
        json.Arr("streams").Select(s => Parse(s, addon)).Where(c => c is not null).Select(c => c!).ToList();

    public static StreamCandidate? Parse(JsonElement s, string addon)
    {
        var infoHash = s.Str("infoHash");
        var url = s.Str("url");
        if (infoHash is null && (url is null || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))) return null;

        var name = s.Str("name") ?? "";
        var title = s.Str("title") ?? s.Str("description") ?? "";
        var hints = s.Prop("behaviorHints");
        var fileName = hints?.Str("filename") ?? FileNameFromTitle(title);

        var tag = InstantTagRx().Match(name);
        var isDirect = url is not null && infoHash is null || tag.Success;
        var instant = tag.Success && tag.Groups[2].Value.Contains('+');

        var headers = new Dictionary<string, string>();
        if (hints?.Prop("proxyHeaders")?.Prop("request") is { ValueKind: JsonValueKind.Object } req)
            foreach (var p in req.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) headers[p.Name] = p.Value.GetString()!;

        var connectionHints = s.StrArr("sources")
            .Where(x => x.StartsWith("tracker:", StringComparison.OrdinalIgnoreCase))
            .Select(x => x[8..]).Where(x => x.Length > 0).ToList();

        var text = name + "\n" + title;
        return new StreamCandidate
        {
            Addon = addon,
            Name = name,
            Title = title,
            Quality = DetectQuality(name + " " + title),
            SizeBytes = ParseSize(title) ?? (hints?.Dbl("videoSize") is { } vs ? (long)vs : null),
            Popularity = ParseAvailability(title),
            Provider = ParseProvider(title),
            Languages = LanguageCodes.Detect(text),
            InfoHash = infoHash?.ToLowerInvariant(),
            FileIdx = s.Int("fileIdx"),
            Url = url,
            ConnectionHints = connectionHints,
            FileName = fileName,
            IsDirect = isDirect,
            IsInstant = instant,
            Headers = headers,
            BingeGroup = hints?.Str("bingeGroup"),
        };
    }

    public static string DetectQuality(string text)
    {
        if (Q2160Rx().IsMatch(text)) return "2160p";
        if (Q1080Rx().IsMatch(text)) return "1080p";
        if (Q720Rx().IsMatch(text)) return "720p";
        if (Q480Rx().IsMatch(text)) return "480p";
        return "SD";
    }

    /// <summary>Orden numérico de una calidad ("2160p" 2160 … "SD" 360).</summary>
    public static int QualityValue(string? quality) => quality?.ToLowerInvariant() switch
    {
        "2160p" or "4k" => 2160, "1440p" => 1440, "1080p" => 1080, "720p" => 720, "480p" => 480, _ => 360,
    };

    public static long? ParseSize(string text)
    {
        var m = SizeRx().Match(text);
        if (!m.Success || !double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return null;
        var mult = m.Groups[2].Value.ToUpperInvariant() switch
        {
            "TB" => 1L << 40, "GB" => 1L << 30, "MB" => 1L << 20, _ => 1L << 10,
        };
        return (long)(n * mult);
    }

    /// <summary>Disponibilidad que el addon escribe tras el icono 👤 en la descripción; null si no aparece.</summary>
    public static int? ParseAvailability(string text)
    {
        var m = AvailabilityRx().Match(text);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
    }

    /// <summary>
    /// Etiquetas legibles del release, en orden: origen ("remux", "bluray", "web", "tv", "dvd", "cam"),
    /// imagen ("hdr", "3d") y audio ("7.1", "5.1").
    /// </summary>
    public static IReadOnlyList<string> ReleaseTags(string text)
    {
        var tags = new List<string>();
        if (RemuxRx().IsMatch(text)) tags.Add("remux");
        else if (BluRayRx().IsMatch(text)) tags.Add("bluray");
        else if (WebRx().IsMatch(text)) tags.Add("web");
        else if (HdtvRx().IsMatch(text)) tags.Add("tv");
        else if (DvdRx().IsMatch(text)) tags.Add("dvd");
        else if (CamRx().IsMatch(text)) tags.Add("cam");
        if (HdrRx().IsMatch(text)) tags.Add("hdr");
        if (ThreeDRx().IsMatch(text)) tags.Add("3d");
        var audio = AudioRx().Match(text);
        if (audio.Success) tags.Add(audio.Groups[1].Value);
        return tags;
    }

    /// <summary>
    /// Año del release según su nombre: el último 19xx/20xx suelto (en "Blade.Runner.2049.2017" es 2017, el título
    /// va antes que el año). Null si no hay.
    /// </summary>
    public static int? ParseYear(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var line = text.Split('\n', 2)[0];
        var matches = YearRx().Matches(line);
        return matches.Count == 0 ? null : int.Parse(matches[^1].Value, CultureInfo.InvariantCulture);
    }

    public static string? ParseProvider(string text)
    {
        var m = ProviderRx().Match(text);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static string? FileNameFromTitle(string title)
    {
        // Formato habitual de los addons: línea 1 = nombre de la publicación; en series la línea 2 es el archivo concreto.
        var lines = title.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return null;
        if (lines.Length > 1 && !lines[1].Contains("👤") && !lines[1].Contains("💾")) return lines[1];
        return lines[0];
    }

    [GeneratedRegex(@"^\s*\[([A-Za-z]{2,3})\s*(\+|download|\s)?\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex InstantTagRx();
    [GeneratedRegex(@"(?<![A-Za-z0-9])(2160p|4k|uhd)(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Q2160Rx();
    [GeneratedRegex(@"(?<![A-Za-z0-9])1080p?(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Q1080Rx();
    [GeneratedRegex(@"(?<![A-Za-z0-9])720p?(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Q720Rx();
    [GeneratedRegex(@"(?<![A-Za-z0-9])(480p|576p|dvdrip)(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Q480Rx();
    [GeneratedRegex(@"💾\s*([\d.,]+)\s*(TB|GB|MB|KB)", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRx();
    [GeneratedRegex(@"(?<![A-Za-z0-9])remux(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex RemuxRx();
    [GeneratedRegex(@"(?<![A-Za-z0-9])(blu-?ray|bdrip|brrip|bd25|bd50)(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex BluRayRx();
    [GeneratedRegex(@"(?<![A-Za-z0-9])(web-?dl|web-?rip|web)(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex WebRx();
    [GeneratedRegex(@"(?<![A-Za-z0-9])(hdtv|pdtv)(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex HdtvRx();
    [GeneratedRegex(@"(?<![A-Za-z0-9])(dvd-?rip|dvd|dvd5|dvd9)(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex DvdRx();
    [GeneratedRegex(@"(?<![A-Za-z0-9])(cam|hdcam|camrip|hdts|telesync|telecine)(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex CamRx();
    [GeneratedRegex(@"(?<![A-Za-z0-9])(hdr10\+?|hdr|dolby[ .]?vision|dv)(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex HdrRx();
    [GeneratedRegex(@"(?<![A-Za-z0-9])(3d|h-?sbs|sbs)(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex ThreeDRx();
    [GeneratedRegex(@"(?<![0-9])([57]\.1)(?![0-9])")]
    private static partial Regex AudioRx();
    [GeneratedRegex(@"(?<![0-9xX])(19[0-9]{2}|20[0-9]{2})(?![0-9xXpP])")]
    private static partial Regex YearRx();
    [GeneratedRegex(@"⚙️\s*([^\n]+)")]
    private static partial Regex ProviderRx();
    [GeneratedRegex(@"👤\s*(\d+)")]
    private static partial Regex AvailabilityRx();
}
