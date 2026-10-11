using Parche.App.ViewModels;

namespace Parche.App.Views;

public partial class MyListPage : ContentPage
{
    public MyListPage(MyListViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        Responsive.FitPosterGrid(Grid, width);
    }
}
