using Microsoft.Extensions.DependencyInjection;
using Parche.App.Localization;
using Parche.App.Services;
using Parche.App.Views;

namespace Parche.App;

public partial class AppShell : Shell
{
    public AppShell(IServiceProvider services)
    {
        InitializeComponent();
        foreach (var (route, title, icon, type) in ShellSetup.Tabs)
        {
            var content = new ShellContent
            {
                Route = route,
                Icon = icon,
                ContentTemplate = new DataTemplate(() => services.GetRequiredService(type)),
            };
            content.SetBinding(BaseShellItem.TitleProperty, new Binding($"[{title}]", source: LocalizationManager.Instance));
            Tabs.Items.Add(content);
        }
        ShellSetup.RegisterRoutes();
    }
}

internal static class ShellSetup
{
    public static readonly (string Route, string TitleKey, string Icon, Type Page)[] Tabs =
    {
        ("home", "Tab_Home", "tab_home.png", typeof(HomePage)),
        ("search", "Tab_Search", "tab_search.png", typeof(SearchPage)),
        ("categories", "Tab_Categories", "tab_categories.png", typeof(CategoriesPage)),
        ("mylist", "Tab_MyList", "tab_mylist.png", typeof(MyListPage)),
        ("settings", "Tab_Settings", "tab_settings.png", typeof(SettingsPage)),
    };

    private static bool _registered;

    public static void RegisterRoutes()
    {
        if (_registered) return;
        _registered = true;
        Routing.RegisterRoute(Routes.Detail, typeof(DetailPage));
        Routing.RegisterRoute(Routes.Streams, typeof(StreamPickerPage));
        Routing.RegisterRoute(Routes.Player, typeof(PlayerPage));
    }
}
