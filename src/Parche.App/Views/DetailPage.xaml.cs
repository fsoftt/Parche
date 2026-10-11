using Parche.App.ViewModels;

namespace Parche.App.Views;

public partial class DetailPage : ContentPage
{
    private readonly DetailViewModel _vm;

    public DetailPage(DetailViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override void OnDisappearing()
    {
        _vm.Detach();
        base.OnDisappearing();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.Attach();
        // En TV el foco inicial va al botón Reproducir, como en Netflix.
        if (Services.Nav.IsTv) Dispatcher.Dispatch(() => PlayButton.Focus());
    }

    private async void OnBack(object? sender, EventArgs e) => await Shell.Current.GoToAsync("..");

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        Responsive.FitHero(Hero, height);
    }
}
