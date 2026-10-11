using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using Parche.App.Controls;

namespace Parche.App.Platforms.Android;

/// <summary>Hace que <see cref="TvCard"/> reciba foco del D-pad y reaccione a OK/Enter y al toque.</summary>
public class TvCardHandler : BorderHandler
{
    protected override void ConnectHandler(ContentViewGroup platformView)
    {
        base.ConnectHandler(platformView);
        platformView.Focusable = true;
        platformView.Clickable = true;
        platformView.FocusableInTouchMode = false;
        platformView.DescendantFocusability = global::Android.Views.DescendantFocusability.BlockDescendants;
        platformView.Click += OnClick;
        platformView.FocusChange += OnFocusChange;
    }

    protected override void DisconnectHandler(ContentViewGroup platformView)
    {
        platformView.Click -= OnClick;
        platformView.FocusChange -= OnFocusChange;
        base.DisconnectHandler(platformView);
    }

    private void OnClick(object? sender, EventArgs e) => (VirtualView as TvCard)?.Activate();

    private void OnFocusChange(object? sender, global::Android.Views.View.FocusChangeEventArgs e) =>
        (VirtualView as TvCard)?.SetHighlighted(e.HasFocus);
}
