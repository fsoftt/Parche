using CommunityToolkit.Mvvm.ComponentModel;

namespace Parche.App.ViewModels;

public abstract partial class BaseViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    [ObservableProperty]
    private bool _isBusy;

    public bool HasError => !string.IsNullOrEmpty(Error);

    public bool IsTv => Services.Nav.IsTv;

    /// <summary>Ejecuta una carga mostrando el indicador y capturando errores para la UI.</summary>
    protected async Task RunAsync(Func<Task> work)
    {
        if (IsBusy) return;
        IsBusy = true;
        Error = null;
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
