using System.Windows.Input;

namespace Parche.App.Controls;

/// <summary>
/// Tarjeta seleccionable con toque (celular) o con el D-pad del control remoto (TV).
/// En Android el handler la vuelve "focusable"; al recibir foco pasa al estado visual <c>Focused</c>. En Windows reacciona al mouse (hover y clic).
/// </summary>
public class TvCard : Border
{
    public static readonly BindableProperty CommandProperty =
        BindableProperty.Create(nameof(Command), typeof(ICommand), typeof(TvCard));

    public static readonly BindableProperty CommandParameterProperty =
        BindableProperty.Create(nameof(CommandParameter), typeof(object), typeof(TvCard));

    public static readonly BindableProperty HighlightCommandProperty =
        BindableProperty.Create(nameof(HighlightCommand), typeof(ICommand), typeof(TvCard));

    public static readonly BindableProperty FocusScaleProperty =
        BindableProperty.Create(nameof(FocusScale), typeof(double), typeof(TvCard), 1.08);

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    /// <summary>Se ejecuta al recibir foco (en TV la portada actualiza el banner con el título enfocado).</summary>
    public ICommand? HighlightCommand
    {
        get => (ICommand?)GetValue(HighlightCommandProperty);
        set => SetValue(HighlightCommandProperty, value);
    }

    public double FocusScale
    {
        get => (double)GetValue(FocusScaleProperty);
        set => SetValue(FocusScaleProperty, value);
    }

    internal void Activate()
    {
        if (Command?.CanExecute(CommandParameter) == true) Command.Execute(CommandParameter);
    }

    /// <param name="notify">Ejecuta <see cref="HighlightCommand"/> al enfocar (en escritorio el simple paso del mouse no debe hacerlo).</param>
    internal void SetHighlighted(bool focused, bool notify = true)
    {
        VisualStateManager.GoToState(this, focused ? "Focused" : "Normal");
        this.AbortAnimation("tvfocus");
        var from = Scale;
        var to = focused ? FocusScale : 1.0;
        this.Animate("tvfocus", v => Scale = from + (to - from) * v, length: 140, easing: Easing.CubicOut);
        ZIndex = focused ? 10 : 0;
        if (notify && focused && HighlightCommand?.CanExecute(CommandParameter) == true) HighlightCommand.Execute(CommandParameter);
    }
}
