namespace Parche.App.Controls;

/// <summary>Botón tipo "chip" con estado seleccionado (relleno violeta). Sigue siendo un Button: recibe foco del D-pad.</summary>
public class Chip : Button
{
    public static readonly BindableProperty IsSelectedProperty =
        BindableProperty.Create(nameof(IsSelected), typeof(bool), typeof(Chip), false,
            propertyChanged: (b, _, n) => ((Chip)b).ApplySelection(n is true));

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    private void ApplySelection(bool selected)
    {
        if (selected)
        {
            BackgroundColor = Application.Current?.Resources.TryGetValue("Accent2", out var c) == true && c is Color accent
                ? accent : Color.FromArgb("#8B5CF6");
            TextColor = Colors.White;
        }
        else
        {
            // Quita el valor local y vuelve a regir el estilo.
            ClearValue(BackgroundColorProperty);
            ClearValue(TextColorProperty);
        }
    }
}
