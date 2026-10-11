using CommunityToolkit.Mvvm.ComponentModel;

namespace Parche.App.ViewModels;

/// <summary>Opción de un grupo de chips (idioma, calidad, género, temporada…).</summary>
public partial class ChipOption(string label, object? value = null) : ObservableObject
{
    public string Label { get; } = label;
    public object? Value { get; } = value;

    [ObservableProperty]
    private bool _isSelected;
}
