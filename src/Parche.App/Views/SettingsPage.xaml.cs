using Parche.App.ViewModels;

namespace Parche.App.Views;

public partial class SettingsPage : ContentPage
{
    private readonly SettingsViewModel _vm;

    public SettingsPage(SettingsViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.Reload(); // descarta lo que quedó sin guardar al salir de la pestaña
    }
}
