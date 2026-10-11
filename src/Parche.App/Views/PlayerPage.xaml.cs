using Parche.App.ViewModels;

namespace Parche.App.Views;

public partial class PlayerPage : ContentPage
{
    private readonly PlayerViewModel _vm;

    public PlayerPage(PlayerViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        Video.PlaybackFailed += async (_, failure) => await _vm.OnNativeFailedAsync(failure);
        Video.PlaybackEnded += async (_, _) => await Shell.Current.GoToAsync("..");
        Video.ControllerVisibilityChanged += (_, visible) => _vm.OnControllerVisibility(visible);
        Video.ProgressChanged += (_, p) => _vm.OnProgress(p);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerViewModel.DiagnosticsVisible) && _vm.DiagnosticsVisible)
                Dispatcher.Dispatch(() => CopyButton.Focus());
            // En TV el foco va a Cancelar durante la preparación y a Reintentar si falla.
            else if (e.PropertyName == nameof(PlayerViewModel.IsBusy) && _vm.IsBusy && _vm.IsResolving)
                Dispatcher.Dispatch(() => CancelButton.Focus());
            else if (e.PropertyName == nameof(PlayerViewModel.HasError) && _vm.HasError)
                Dispatcher.Dispatch(() => RetryButton.Focus());
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        FullScreen.Enter();
        await _vm.StartAsync();
    }

    protected override void OnDisappearing()
    {
        _vm.Stop();
        FullScreen.Exit();
        base.OnDisappearing();
    }

    /// <summary>Mientras se navega por la barra con el D-pad no debe ocultarse.</summary>
    private void OnBarFocused(object? sender, FocusEventArgs e) => _vm.ShowOverlay();

    /// <summary>Atrás cierra primero el panel de diagnóstico.</summary>
    protected override bool OnBackButtonPressed()
    {
        if (_vm.DiagnosticsVisible)
        {
            _vm.CloseDiagnosticsCommand.Execute(null);
            return true;
        }
        return base.OnBackButtonPressed();
    }
}
