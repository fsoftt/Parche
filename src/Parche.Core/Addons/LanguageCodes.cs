using System.Text.RegularExpressions;

namespace Parche.Core.Addons;

/// <summary>Detección de idiomas en nombres de release/banderas y conversión de códigos de subtítulos.</summary>
public static partial class LanguageCodes
{
    public const string Multi = "multi";

    private static readonly Dictionary<string, string> FlagMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ES"] = "es",
        ["MX"] = "es-419", ["AR"] = "es-419", ["CO"] = "es-419", ["CL"] = "es-419", ["PE"] = "es-419",
        ["VE"] = "es-419", ["UY"] = "es-419", ["EC"] = "es-419", ["BO"] = "es-419", ["PY"] = "es-419",
        ["CR"] = "es-419", ["PA"] = "es-419", ["DO"] = "es-419", ["GT"] = "es-419", ["CU"] = "es-419",
        ["GB"] = "en", ["US"] = "en", ["AU"] = "en", ["CA"] = "en",
        ["BR"] = "pt", ["PT"] = "pt",
        ["FR"] = "fr", ["IT"] = "it", ["DE"] = "de", ["RU"] = "ru", ["JP"] = "ja", ["KR"] = "ko", ["CN"] = "zh", ["IN"] = "hi",
    };

    /// <summary>Extrae los idiomas de <paramref name="text"/> (nombre + título del addon). Sin información devuelve {"en"}.</summary>
    public static HashSet<string> Detect(string? text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        text ??= "";

        // Banderas: pares de indicadores regionales (U+1F1E6..U+1F1FF).
        for (var i = 0; i < text.Length - 3; i++)
        {
            if (char.IsHighSurrogate(text[i]) && char.IsHighSurrogate(text[i + 2]))
            {
                var a = char.ConvertToUtf32(text[i], text[i + 1]);
                var b = char.ConvertToUtf32(text[i + 2], text[i + 3]);
                if (a is >= 0x1F1E6 and <= 0x1F1FF && b is >= 0x1F1E6 and <= 0x1F1FF)
                {
                    var code = new string(new[] { (char)('A' + a - 0x1F1E6), (char)('A' + b - 0x1F1E6) });
                    if (FlagMap.TryGetValue(code, out var lang)) set.Add(lang);
                    i += 3;
                }
            }
        }

        if (LatinoRx().IsMatch(text)) set.Add("es-419");
        if (SpanishRx().IsMatch(text)) set.Add("es");
        if (EnglishRx().IsMatch(text)) set.Add("en");
        if (PortugueseRx().IsMatch(text)) set.Add("pt");
        if (MultiRx().IsMatch(text)) set.Add(Multi);

        if (set.Count == 0) set.Add("en");
        return set;
    }

    // Subtítulos: códigos ISO 639-2 (3 letras) de OpenSubtitles → ISO 639-1 y nombre.
    private static readonly Dictionary<string, (string Iso1, string Name)> Subs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["spa"] = ("es", "Español"), ["spl"] = ("es-419", "Español (Latino)"), ["eng"] = ("en", "English"),
        ["por"] = ("pt", "Português"), ["pob"] = ("pt-BR", "Português (Brasil)"), ["fre"] = ("fr", "Français"),
        ["fra"] = ("fr", "Français"), ["ger"] = ("de", "Deutsch"), ["deu"] = ("de", "Deutsch"), ["ita"] = ("it", "Italiano"),
        ["rus"] = ("ru", "Русский"), ["jpn"] = ("ja", "日本語"), ["kor"] = ("ko", "한국어"), ["ara"] = ("ar", "العربية"),
        ["nld"] = ("nl", "Nederlands"), ["dut"] = ("nl", "Nederlands"), ["pol"] = ("pl", "Polski"), ["tur"] = ("tr", "Türkçe"),
        ["swe"] = ("sv", "Svenska"), ["ell"] = ("el", "Ελληνικά"), ["gre"] = ("el", "Ελληνικά"), ["heb"] = ("he", "עברית"),
        ["zho"] = ("zh", "中文"), ["chi"] = ("zh", "中文"), ["zht"] = ("zh", "中文 (繁體)"), ["cat"] = ("ca", "Català"),
    };

    /// <summary>ISO 639-2 → ISO 639-1 (si no se conoce devuelve el código tal cual en minúsculas).</summary>
    public static string ToIso1(string code)
    {
        code = (code ?? "").Trim();
        if (Subs.TryGetValue(code, out var v)) return v.Iso1;
        return code.ToLowerInvariant();
    }

    public static string DisplayName(string code)
    {
        if (Subs.TryGetValue(code, out var v)) return v.Name;
        var iso = code.Trim();
        if (Subs.Values.FirstOrDefault(x => x.Iso1.Equals(iso, StringComparison.OrdinalIgnoreCase)) is { Name: not null } m) return m.Name;
        return iso.ToUpperInvariant();
    }

    /// <summary>true si el código de subtítulo <paramref name="subCode"/> coincide con el deseado <paramref name="wanted"/> (2 o 3 letras).</summary>
    public static bool SameLanguage(string subCode, string wanted) =>
        string.Equals(ToIso1(subCode), ToIso1(wanted), StringComparison.OrdinalIgnoreCase);

    /// <summary>¿<paramref name="candidate"/> satisface la preferencia <paramref name="preferred"/>? "es" acepta también "es-419".</summary>
    public static bool Matches(string preferred, string candidate) =>
        string.Equals(preferred, candidate, StringComparison.OrdinalIgnoreCase)
        || (!preferred.Contains('-') && candidate.StartsWith(preferred + "-", StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"(?<![A-Za-z])(LATINO|LATIN|LAT|LATAM)(?![A-Za-z])", RegexOptions.IgnoreCase)]
    private static partial Regex LatinoRx();
    [GeneratedRegex(@"(?<![A-Za-z])(SPANISH|ESPA[ÑN]OL|ESP|CASTELLANO)(?![A-Za-z])", RegexOptions.IgnoreCase)]
    private static partial Regex SpanishRx();
    [GeneratedRegex(@"(?<![A-Za-z])(ENGLISH|ENG)(?![A-Za-z])", RegexOptions.IgnoreCase)]
    private static partial Regex EnglishRx();
    [GeneratedRegex(@"(?<![A-Za-z])(PORTUGUESE|PORTUGU[EÊ]S|DUBLADO|PT-BR|PTBR)(?![A-Za-z])", RegexOptions.IgnoreCase)]
    private static partial Regex PortugueseRx();
    [GeneratedRegex(@"(?<![A-Za-z])(DUAL|MULTI)(?![A-Za-z])", RegexOptions.IgnoreCase)]
    private static partial Regex MultiRx();
}
