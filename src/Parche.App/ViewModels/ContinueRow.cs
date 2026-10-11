using Parche.App.Localization;
using Parche.Core.Library;

namespace Parche.App.ViewModels;

/// <summary>Tarjeta de "Continuar viendo": imagen, título, episodio o tiempo restante y barra de avance.</summary>
public sealed class ContinueRow(WatchEntry entry)
{
    public WatchEntry Entry { get; } = entry;
    public string? Image => Entry.Item.Wide;
    public string Title => Entry.Item.Title;
    public double Fraction => Entry.Fraction;

    /// <summary>"T1:E2 · Título" en series; "Quedan 45 min" en películas (si se conoce la duración).</summary>
    public string Subtitle => Entry.Season is { } s && Entry.Episode is { } e
        ? LocalizationManager.T("Detail_EpisodeLabel", s, e, Entry.EpisodeTitle ?? "")
        : Entry.DurationMs > 0
            ? LocalizationManager.T("Home_MinutesLeft", Math.Max(1, (Entry.DurationMs - Entry.PositionMs) / 60_000))
            : "";
}
