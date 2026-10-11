namespace Parche.App.Views;

/// <summary>Ajustes de diseño que dependen del tamaño real de la pantalla (celular en horizontal).</summary>
internal static class Responsive
{
    private const double PosterWidth = 112;

    /// <summary>Columnas de la grilla de pósters: 3 en celular vertical, más cuando hay ancho (horizontal). En TV no cambia.</summary>
    public static void FitPosterGrid(GridItemsLayout grid, double width)
    {
        if (DeviceInfo.Idiom == DeviceIdiom.TV || width <= 0) return;
        var span = Math.Max(3, (int)(width / PosterWidth));
        if (grid.Span != span) grid.Span = span;
    }

    /// <summary>La imagen principal no ocupa más del 45 % del alto (en horizontal tapaba los botones).</summary>
    public static void FitHero(View hero, double height)
    {
        if (DeviceInfo.Idiom == DeviceIdiom.TV || height <= 0) return;
        var h = Math.Min(300, Math.Round(height * 0.45));
        if (Math.Abs(hero.HeightRequest - h) > 1) hero.HeightRequest = h;
    }
}
