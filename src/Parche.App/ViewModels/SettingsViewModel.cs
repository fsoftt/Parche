using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Parche.App.Localization;
using Parche.App.Plugins;
using Parche.App.Services;
using Parche.Core.Settings;

namespace Parche.App.ViewModels;

public partial class SettingsViewModel : BaseViewModel
{
    private readonly SettingsService _settings;
    private readonly IServiceProvider _services;
    private AppSettings _draft;

    // Idiomas preferidos de las fuentes: códigos que usa el ranking, en orden de preferencia.
    private static readonly (string Code, string Key)[] SourceLangs =
    {
        ("es-419", "Streams_LangLatino"),
        ("es", "Streams_LangEs"),
        ("en", "Streams_LangEn"),
        ("pt", "Lang_Portuguese"),
    };

    private static readonly (string Code, string Key)[] SubLangs =
    {
        ("spa", "Lang_Spanish"), ("eng", "Lang_English"), ("por", "Lang_Portuguese"),
    };

    private static readonly string[] Qualities = { "2160p", "1080p", "720p" };

    public SettingsViewModel(SettingsService settings, IServiceProvider services, IEnumerable<IPluginSettingsSection> sections)
    {
        _settings = settings;
        _services = services;
        _sections = sections.ToList();
        _draft = settings.Clone();
        Populate(_draft);
        // Al cambiar el idioma se rehacen las etiquetas conservando lo editado sin guardar.
        LocalizationManager.Instance.PropertyChanged += (_, _) => MainThread.BeginInvokeOnMainThread(() =>
        {
            ApplyForm(_draft);
            Populate(_draft);
        });
    }

    private readonly IReadOnlyList<IPluginSettingsSection> _sections;

    // ----- idioma de la interfaz -----
    public bool IsLangSystem => LocalizationManager.Instance.Language == "";
    public bool IsLangEs => LocalizationManager.Instance.Language == "es";
    public bool IsLangEn => LocalizationManager.Instance.Language == "en";
    public bool IsLangPt => LocalizationManager.Instance.Language == "pt";

    // ----- grupos de chips -----
    public ObservableCollection<ChipOption> CatalogOptions { get; } = new();
    public ObservableCollection<ChipOption> SourceLangOptions { get; } = new();
    public ObservableCollection<ChipOption> QualityOptions { get; } = new();
    public ObservableCollection<ChipOption> SubtitleOptions { get; } = new();

    // ----- listas editables -----
    public ObservableCollection<UrlRow> StreamAddonRows { get; } = new();
    public ObservableCollection<UrlRow> SubtitleAddonRows { get; } = new();

    // ----- plugins -----
    public ObservableCollection<PluginSectionRow> PluginSections { get; } = new();
    public bool HasPluginSections => PluginSections.Count > 0;

    [ObservableProperty]
    private string _tmdbKey = "";

    [ObservableProperty]
    private bool _downloadWifiOnly = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    private string _problems = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusText = "";

    public bool HasProblems => Problems.Length > 0;
    public bool HasStatus => StatusText.Length > 0;

    /// <summary>Recarga el formulario desde los ajustes guardados (descarta cambios sin guardar).</summary>
    public void Reload()
    {
        _draft = _settings.Clone();
        Populate(_draft);
    }

    private void Populate(AppSettings s)
    {
        Fill(CatalogOptions, new (string, object?)[]
        {
            (LocalizationManager.T("Settings_Auto"), CatalogProviderKind.Auto),
            ("TMDB", CatalogProviderKind.Tmdb),
            ("Cinemeta", CatalogProviderKind.Cinemeta),
        }, o => (CatalogProviderKind)o! == s.CatalogProvider);

        Fill(SourceLangOptions, SourceLangs.Select(l => (LocalizationManager.T(l.Key), (object?)l.Code)),
            o => s.PreferredLanguages.Contains((string)o!, StringComparer.OrdinalIgnoreCase));

        Fill(QualityOptions, Qualities.Select(q => (q, (object?)q)),
            o => string.Equals((string)o!, s.PreferredMaxQuality, StringComparison.OrdinalIgnoreCase));

        Fill(SubtitleOptions, SubLangs.Select(l => (LocalizationManager.T(l.Key), (object?)l.Code)),
            o => s.SubtitleLanguages.Contains((string)o!, StringComparer.OrdinalIgnoreCase));

        TmdbKey = s.TmdbApiKey ?? "";
        DownloadWifiOnly = s.DownloadOnlyOnWifi;

        StreamAddonRows.Clear();
        foreach (var url in s.StreamAddons) StreamAddonRows.Add(new UrlRow(url));
        SubtitleAddonRows.Clear();
        foreach (var url in s.SubtitleAddons) SubtitleAddonRows.Add(new UrlRow(url));

        PluginSections.Clear();
        foreach (var section in _sections) PluginSections.Add(new PluginSectionRow(section));
        OnPropertyChanged(nameof(HasPluginSections));

        OnPropertyChanged(nameof(IsLangSystem));
        OnPropertyChanged(nameof(IsLangEs));
        OnPropertyChanged(nameof(IsLangEn));
        OnPropertyChanged(nameof(IsLangPt));
    }

    private static void Fill(ObservableCollection<ChipOption> target, IEnumerable<(string Label, object? Value)> items, Func<object?, bool> selected)
    {
        target.Clear();
        foreach (var (label, value) in items) target.Add(new ChipOption(label, value) { IsSelected = selected(value) });
    }

    private static void SelectOne(ObservableCollection<ChipOption> group, ChipOption chosen)
    {
        foreach (var o in group) o.IsSelected = ReferenceEquals(o, chosen);
    }

    [RelayCommand]
    private void SetLanguage(string? code)
    {
        LocalizationManager.Instance.SetLanguage(code); // refresca los textos; Reload se dispara por el cambio de idioma
    }

    [RelayCommand]
    private void SelectCatalog(ChipOption o) => SelectOne(CatalogOptions, o);

    [RelayCommand]
    private void SelectQuality(ChipOption o) => SelectOne(QualityOptions, o);

    [RelayCommand]
    private void Toggle(ChipOption o) => o.IsSelected = !o.IsSelected;

    [RelayCommand]
    private void AddStreamAddon() => StreamAddonRows.Add(new UrlRow());

    [RelayCommand]
    private void RemoveStreamAddon(UrlRow row) => StreamAddonRows.Remove(row);

    [RelayCommand]
    private void AddSubtitleAddon() => SubtitleAddonRows.Add(new UrlRow());

    [RelayCommand]
    private void RemoveSubtitleAddon(UrlRow row) => SubtitleAddonRows.Remove(row);

    /// <summary>Abre la página de ajustes que aporta un plugin.</summary>
    [RelayCommand]
    private async Task OpenPluginSectionAsync(PluginSectionRow row)
    {
        try { await Shell.Current.Navigation.PushAsync(row.Section.CreatePage(_services)); }
        catch (Exception ex) { Problems = ex.Message; }
    }

    private static List<string> Urls(IEnumerable<UrlRow> rows) =>
        rows.Select(r => r.Url?.Trim() ?? "").Where(u => u.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Vuelca el formulario en <paramref name="s"/> (sin validar).</summary>
    private void ApplyForm(AppSettings s)
    {
        s.CatalogProvider = Selected(CatalogOptions, CatalogProviderKind.Auto);
        s.TmdbApiKey = string.IsNullOrWhiteSpace(TmdbKey) ? null : TmdbKey.Trim();

        // El ranking prefiere los idiomas elegidos, en ese orden; sin ninguno se conservan los valores por defecto.
        var langs = SourceLangs.Where(l => SourceLangOptions.Any(o => o.IsSelected && (string?)o.Value == l.Code)).Select(l => l.Code).ToList();
        s.PreferredLanguages = langs.Count > 0 ? langs : new() { "es-419", "es", "en" };

        s.PreferredMaxQuality = Selected(QualityOptions, "1080p");
        s.SubtitleLanguages = SubLangs.Where(l => SubtitleOptions.Any(o => o.IsSelected && (string?)o.Value == l.Code)).Select(l => l.Code).ToList();

        s.DownloadOnlyOnWifi = DownloadWifiOnly;
        s.StreamAddons = Urls(StreamAddonRows);
        s.SubtitleAddons = Urls(SubtitleAddonRows);
    }

    [RelayCommand]
    private void Save()
    {
        var s = _draft;
        ApplyForm(s);

        var errors = s.Validate();
        if (errors.Count > 0)
        {
            Problems = string.Join("\n", errors.Select(e => "• " + e));
            StatusText = "";
            return;
        }

        Problems = "";
        _settings.Save(s);
        Reload();
        StatusText = LocalizationManager.T("Settings_Saved");
    }

    private static T Selected<T>(ObservableCollection<ChipOption> group, T fallback) =>
        group.FirstOrDefault(o => o.IsSelected)?.Value is T v ? v : fallback;
}
