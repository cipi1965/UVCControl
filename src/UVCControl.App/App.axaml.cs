using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using UVCControl.App.Views;

namespace UVCControl.App;

public partial class App : Application
{
    private readonly IServiceProvider? _services;

    /// <summary>Used by the XAML previewer, which runs without the host.</summary>
    public App() { }

    public App(IServiceProvider? services) => _services = services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && _services is not null)
            desktop.MainWindow = _services.GetRequiredService<MainWindow>();

        base.OnFrameworkInitializationCompleted();
    }
}
