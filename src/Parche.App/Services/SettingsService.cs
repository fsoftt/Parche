using System.ComponentModel;
using Parche.App.Localization;
using Parche.Core.Settings;

namespace Parche.App.Services;

/// <summary>
/// Ajustes de la app (<see cref="AppSettings"/> en JSON dentro de Preferences). El idioma de los metadatos
/// sigue al idioma de la interfaz elegido en <see cref="LocalizationManager"/>.
/// </summary>
public sealed class SettingsService
{
    private const string Key = "app_settings";
    private readonly object _saveGate = new();

    public SettingsService()
    {
        string json;
        try { json = Preferences.Default.Get(Key, ""); }
        catch (Exception) { json = ""; }
        Current = AppSettings.FromJson(json);
        Current.AppLanguage = LanguageForUi();
        LocalizationManager.Instance.PropertyChanged += OnLocalizationChanged;
    }

    /// <summary>Ajustes vigentes. No se modifican en sitio: edita una copia (<see cref="Clone"/>) y llama a <see cref="Save"/>.</summary>
    public AppSettings Current { get; private set; }

    /// <summary>Se dispara tras guardar ajustes o cambiar el idioma (puede venir de cualquier hilo).</summary>
    public event EventHandler? Changed;

    public AppSettings Clone() => AppSettings.FromJson(Current.ToJson());

    public void Save(AppSettings settings)
    {
        settings.Normalize();
        settings.AppLanguage = LanguageForUi();
        Current = settings;
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        var lang = LanguageForUi();
        if (lang == Current.AppLanguage) return;
        var next = Clone();
        next.AppLanguage = lang;
        Current = next;
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Persist()
    {
        // JSON y escritura fuera del hilo principal; _saveGate conserva el orden de las escrituras.
        var settings = Current;
        Task.Run(() =>
        {
            lock (_saveGate)
            {
                try { Preferences.Default.Set(Key, settings.ToJson()); }
                catch (Exception) { /* sin almacenamiento: los ajustes valen solo en esta sesión */ }
            }
        });
    }

    /// <summary>es → es-ES, en → en-US, pt → pt-BR; cualquier otro idioma usa el español (el idioma neutro de la UI).</summary>
    private static string LanguageForUi() =>
        LocalizationManager.Instance.CurrentCulture.TwoLetterISOLanguageName switch
        {
            "en" => "en-US",
            "pt" => "pt-BR",
            _ => AppSettings.DefaultLanguage,
        };
}
