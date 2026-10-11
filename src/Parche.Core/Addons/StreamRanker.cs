namespace Parche.Core.Addons;

/// <summary>Filtra y ordena candidatos: disponibles al instante, disponibilidad (de más a menos), idioma, calidad (con tope preferido) y tamaño.</summary>
public static class StreamRanker
{
    public static readonly IReadOnlyList<string> DefaultLanguages = new[] { "es-419", "es", "en" };
    public const string DefaultMaxQuality = "1080p";

    /// <summary>
    /// Ordena sin descartar nada: los idiomas preferidos solo desempatan, porque las fuentes más disponibles suelen
    /// estar en otro idioma y ocultarlas deja solo opciones lentas. <paramref name="preferredMaxQuality"/> null o vacío = sin tope.
    /// </summary>
    public static IReadOnlyList<StreamCandidate> Rank(IEnumerable<StreamCandidate> candidates,
        IReadOnlyList<string>? preferredLanguages = null, string? preferredMaxQuality = DefaultMaxQuality)
    {
        var langs = preferredLanguages is { Count: > 0 } ? preferredLanguages : DefaultLanguages;
        var max = string.IsNullOrWhiteSpace(preferredMaxQuality) ? int.MaxValue : StreamParser.QualityValue(preferredMaxQuality);

        return candidates
            .OrderByDescending(c => c.IsInstant)
            .ThenByDescending(c => c.Popularity ?? 0)
            .ThenBy(c => LanguageRank(c, langs))
            .ThenByDescending(c => QualityScore(c, max))
            .ThenByDescending(c => c.SizeBytes ?? 0)
            .ToList();
    }

    /// <summary>
    /// Descarta las fuentes cuyo nombre indica otro año (±1) que el de la película: los addons mezclan remakes y
    /// homónimos bajo el mismo id (p. ej. un "2026 CAMRip" en la ficha de la de 1968). Sin año en el nombre, se conserva.
    /// </summary>
    public static IEnumerable<StreamCandidate> DropOtherYears(IEnumerable<StreamCandidate> candidates, int year) =>
        candidates.Where(c => StreamParser.ParseYear(c.FileName ?? c.Title) is not { } y || Math.Abs(y - year) <= 1);

    /// <summary>Posición de la mejor preferencia que cumple (0 = la primera). "multi" cuenta como tras todas las preferidas; sin coincidencia = Count + 1.</summary>
    internal static int LanguageRank(StreamCandidate c, IReadOnlyList<string> preferred)
    {
        for (var i = 0; i < preferred.Count; i++)
            if (c.Languages.Any(l => LanguageCodes.Matches(preferred[i], l))) return i;
        return c.Languages.Contains(LanguageCodes.Multi) ? preferred.Count : preferred.Count + 1;
    }

    /// <summary>Calidades dentro del tope puntúan por su valor (mayor mejor); las que lo exceden quedan después, de menor a mayor.</summary>
    internal static int QualityScore(StreamCandidate c, int max)
    {
        var q = StreamParser.QualityValue(c.Quality);
        return q <= max ? 10000 + q : 10000 - q;
    }
}
