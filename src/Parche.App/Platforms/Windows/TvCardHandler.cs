using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using Microsoft.UI.Xaml.Input;
using Parche.App.Controls;

namespace Parche.App.Platforms.Windows;

/// <summary>Escritorio: <see cref="TvCard"/> reacciona al paso del mouse (resalte) y al clic.</summary>
public class TvCardHandler : BorderHandler
{
    protected override void ConnectHandler(ContentPanel platformView)
    {
        base.ConnectHandler(platformView);
        platformView.Tapped += OnTapped;
        platformView.PointerEntered += OnPointerEntered;
        platformView.PointerExited += OnPointerExited;
    }

    protected override void DisconnectHandler(ContentPanel platformView)
    {
        platformView.Tapped -= OnTapped;
        platformView.PointerEntered -= OnPointerEntered;
        platformView.PointerExited -= OnPointerExited;
        base.DisconnectHandler(platformView);
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e) => (VirtualView as TvCard)?.Activate();

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => (VirtualView as TvCard)?.SetHighlighted(true, notify: false);

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => (VirtualView as TvCard)?.SetHighlighted(false, notify: false);
}
