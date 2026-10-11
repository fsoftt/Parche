using Microsoft.Extensions.DependencyInjection;
using Parche.App.Localization;

namespace Parche.App;

/// <summary>Shell para Android TV: menú lateral fijo, navegable con el D-pad.</summary>
public partial class TvShell : Shell
{
    public TvShell(IServiceProvider services)
    {
        InitializeComponent();
        foreach (var (route, title, icon, type) in ShellSetup.Tabs)
        {
            var item = new FlyoutItem
            {
                Route = route,
                Icon = icon,
                Items = { new ShellContent { Route = route + "_c", ContentTemplate = new DataTemplate(() => services.GetRequiredService(type)) } },
            };
            item.SetBinding(BaseShellItem.TitleProperty, new Binding($"[{title}]", source: LocalizationManager.Instance));
            Items.Add(item);
        }
        ShellSetup.RegisterRoutes();
    }
}
