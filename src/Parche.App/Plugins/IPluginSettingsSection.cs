namespace Parche.App.Plugins;

/// <summary>
/// Entrada que un plugin agrega en Ajustes: una fila con título que abre su propia página de configuración.
/// </summary>
public interface IPluginSettingsSection
{
    /// <summary>Clave única del plugin.</summary>
    string Key { get; }

    /// <summary>Título en el idioma actual de la interfaz (se consulta cada vez que se muestra).</summary>
    string Title { get; }

    /// <summary>Descripción corta opcional, en el idioma actual.</summary>
    string? Description { get; }

    /// <summary>Crea la página de ajustes del plugin (construida en código).</summary>
    Page CreatePage(IServiceProvider services);
}
