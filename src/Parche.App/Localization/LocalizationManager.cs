using System.ComponentModel;
using System.Globalization;
using System.Resources;

[assembly: NeutralResourcesLanguage("es")]

namespace Parche.App.Localization;

/// <summary>
/// Idioma de la interfaz. Los textos viven en Strings.resx (español, neutro), Strings.en.resx y Strings.pt.resx.
/// En XAML se usa <c>{l:Translate Clave}</c>; en C# <c>LocalizationManager.T("Clave")</c>.
/// Cambiar el idioma refresca en vivo todos los bindings.
/// </summary>
public sealed class LocalizationManager : INotifyPropertyChanged
{
    public const string PreferenceKey = "app_language";

    /// <summary>Códigos soportados. "" = seguir el idioma del sistema. Para agregar uno nuevo: Strings.xx.resx + aquí.</summary>
    public static readonly string[] Supported = { "", "es", "en", "pt" };

    private static readonly Lazy<LocalizationManager> _instance = new(() => new LocalizationManager());
    public static LocalizationManager Instance => _instance.Value;

    private readonly ResourceManager _resources =
        new("Parche.App.Localization.Strings", typeof(LocalizationManager).Assembly);

    private readonly CultureInfo _systemCulture = CultureInfo.CurrentUICulture;
    private CultureInfo _culture;

    private LocalizationManager()
    {
        _culture = _systemCulture;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Código elegido: "" (sistema), "es", "en" o "pt".</summary>
    public string Language { get; private set; } = "";

    public CultureInfo CurrentCulture => _culture;

    public string this[string key]
    {
        get
        {
            try
            {
                return _resources.GetString(key, _culture) ?? key;
            }
            catch (MissingManifestResourceException)
            {
                return key;
            }
        }
    }

    /// <summary>Lee el idioma guardado y lo aplica. Llamar al iniciar la app.</summary>
    public static void Initialize()
    {
        var code = Preferences.Default.Get(PreferenceKey, "");
        Instance.Apply(code, save: false);
    }

    public void SetLanguage(string? code) => Apply(code, save: true);

    private void Apply(string? code, bool save)
    {
        code = (code ?? "").Trim().ToLowerInvariant();
        if (Array.IndexOf(Supported, code) < 0) code = "";

        Language = code;
        _culture = code.Length == 0 ? _systemCulture : CultureInfo.GetCultureInfo(code);
        CultureInfo.CurrentUICulture = _culture;
        CultureInfo.DefaultThreadCurrentUICulture = _culture;

        if (save) Preferences.Default.Set(PreferenceKey, code);

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public static string T(string key) => Instance[key];

    public static string T(string key, params object[] args) =>
        string.Format(Instance.CurrentCulture, Instance[key], args);
}
