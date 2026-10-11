using Parche.App.Localization;
using Parche.Core.Models;

namespace Parche.App.ViewModels;

/// <summary>Formatos de texto compartidos por los view models.</summary>
internal static class Format
{
    public static string TypeName(MediaType type) =>
        LocalizationManager.T(type == MediaType.Series ? "Type_Series" : "Type_Movie");

    public static string Rating(double? rating) => rating is { } r and > 0 ? $"★ {r:0.0}" : "";

    public static string Runtime(int? minutes) => minutes switch
    {
        null or <= 0 => "",
        < 60 => $"{minutes} min",
        _ => $"{minutes / 60} h {minutes % 60:00} min",
    };

    public static string Size(long? bytes) => bytes switch
    {
        null or <= 0 => "",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        _ => $"{bytes / (double)(1L << 20):0} MB",
    };

    public static string Join(params string?[] parts) =>
        string.Join("  ·  ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
