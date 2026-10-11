using Parche.App.ViewModels;

namespace Parche.App.Views;

public partial class SearchPage : ContentPage
{
    public SearchPage(SearchViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // Con un pequeño retraso: en Windows el foco pedido antes de que la pestaña termine de mostrarse se pierde.
        if (string.IsNullOrEmpty(QueryEntry.Text)) Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(250), () => QueryEntry.Focus());
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        Responsive.FitPosterGrid(Grid, width);
    }
}
