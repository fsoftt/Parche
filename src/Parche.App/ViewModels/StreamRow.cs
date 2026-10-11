using Parche.App.Localization;
using Parche.Core.Addons;

namespace Parche.App.ViewModels;

/// <summary>
/// Fila del selector de fuentes. Se describe como una fuente (tipo de copia, tamaño, origen y disponibilidad),
/// sin nombres de archivo ni términos técnicos.
/// </summary>
public sealed class StreamRow
{
    public StreamRow(StreamCandidate c)
    {
        Candidate = c;
        Quality = c.Quality;
        var tags = StreamParser.ReleaseTags(string.IsNullOrWhiteSpace(c.FileName) ? c.Title : c.FileName + "\n" + c.Title);
        var title = Format.Join(tags.Select(TagLabel).ToArray());
        Title = title.Length > 0 ? title : LocalizationManager.T("Streams_Source");
        Languages = string.Join("  ", c.Languages.OrderBy(LangOrder).Select(LanguageLabel));
        Details = Format.Join(Format.Size(c.SizeBytes),
            c.Provider is { Length: > 0 } p ? LocalizationManager.T("Streams_Origin", p) : null);
        (Availability, AvailabilityColor) = Describe(c);
    }

    public StreamCandidate Candidate { get; }
    public string Quality { get; }
    public string Title { get; }
    public string Languages { get; }
    public string Details { get; }
    public string Availability { get; }
    public Color AvailabilityColor { get; }
    public bool HasAvailability => Availability.Length > 0;

    private static (string, Color) Describe(StreamCandidate c)
    {
        if (c.IsInstant) return (LocalizationManager.T("Streams_Cached"), Color.FromArgb("#34D399"));
        if (c.IsDirect) return (LocalizationManager.T("Streams_NeedsPreparing"), Color.FromArgb("#8B5CF6"));
        if (!c.NeedsResolver) return ("", Colors.Transparent);
        return c.Popularity switch
        {
            >= 50 => (LocalizationManager.T("Streams_AvailHigh"), Color.FromArgb("#34D399")),
            >= 10 => (LocalizationManager.T("Streams_AvailMedium"), Color.FromArgb("#FBBF24")),
            _ => (LocalizationManager.T("Streams_AvailLow"), Color.FromArgb("#EC4899")),
        };
    }

    private static string TagLabel(string tag) => tag switch
    {
        "remux" => LocalizationManager.T("Streams_TagRemux"),
        "bluray" => "Blu-ray",
        "web" => "Web",
        "tv" => "TV",
        "dvd" => "DVD",
        "cam" => LocalizationManager.T("Streams_TagCam"),
        "hdr" => "HDR",
        "3d" => "3D",
        _ => LocalizationManager.T("Streams_TagAudio", tag),
    };

    private static int LangOrder(string code) => code switch { "es-419" => 0, "es" => 1, "en" => 2, "pt" => 3, _ => 9 };

    public static string LanguageLabel(string code) => code switch
    {
        "es" => Flag("🇪🇸") + LocalizationManager.T("Streams_LangEs"),
        "es-419" => Flag("🇲🇽") + LocalizationManager.T("Streams_LangLatino"),
        "en" => Flag("🇬🇧") + LocalizationManager.T("Streams_LangEn"),
        "pt" => Flag("🇧🇷") + LocalizationManager.T("Streams_LangPt"),
        "multi" => "🌐 " + LocalizationManager.T("Streams_LangMulti"),
        _ => LanguageCodes.DisplayName(code),
    };

    // Windows no dibuja banderas emoji (muestra las letras "GB", "ES"…): allí solo va el nombre del idioma.
    private static string Flag(string emoji) => DeviceInfo.Platform == DevicePlatform.WinUI ? "" : emoji + " ";
}
