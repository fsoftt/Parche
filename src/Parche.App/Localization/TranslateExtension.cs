namespace Parche.App.Localization;

/// <summary>Uso en XAML: <c>Text="{l:Translate Common_Play}"</c>. Se actualiza solo al cambiar de idioma.</summary>
[ContentProperty(nameof(Key))]
public class TranslateExtension : IMarkupExtension<BindingBase>
{
    public string Key { get; set; } = "";
    public string? StringFormat { get; set; }

    public BindingBase ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]", BindingMode.OneWay, source: LocalizationManager.Instance, stringFormat: StringFormat);

    object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) => ProvideValue(serviceProvider);
}
