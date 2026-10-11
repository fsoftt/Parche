using CommunityToolkit.Mvvm.ComponentModel;

namespace Parche.App.ViewModels;

/// <summary>Una URL editable en una lista de Ajustes (addons de fuentes o de subtítulos).</summary>
public partial class UrlRow(string url = "") : ObservableObject
{
    [ObservableProperty]
    private string _url = url;
}

/// <summary>Entrada de Ajustes que aporta un plugin (<see cref="Plugins.IPluginSettingsSection"/>).</summary>
public sealed class PluginSectionRow(Plugins.IPluginSettingsSection section)
{
    public Plugins.IPluginSettingsSection Section { get; } = section;
    public string Title => Section.Title;
    public string Description => Section.Description ?? "";
    public bool HasDescription => !string.IsNullOrEmpty(Section.Description);
}
