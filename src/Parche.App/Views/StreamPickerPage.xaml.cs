using Parche.App.ViewModels;

namespace Parche.App.Views;

public partial class StreamPickerPage : ContentPage
{
    private readonly StreamPickerViewModel _vm;

    public StreamPickerPage(StreamPickerViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        // En TV, el foco pasa al botón "Reproducir el mejor" en cuanto hay fuentes.
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StreamPickerViewModel.HasStreams) && _vm.HasStreams && Services.Nav.IsTv)
                Dispatcher.Dispatch(() => BestButton.Focus());
        };
    }

    private async void OnBack(object? sender, EventArgs e) => await Shell.Current.GoToAsync("..");
}
