using Parche.Core.Settings;

namespace Parche.Core.Addons;

/// <summary>
/// Aporta direcciones de addons de fuentes además de las que agregó el usuario en <see cref="AppSettings.StreamAddons"/>.
/// Un plugin lo registra en el contenedor de servicios; el motor consulta a todos los proveedores y une los resultados.
/// </summary>
public interface IStreamSourceProvider
{
    /// <summary>URLs base de los addons que este proveedor ofrece con los ajustes dados (vacío si no aplica).</summary>
    IEnumerable<string> GetAddonUrls(AppSettings settings);
}
