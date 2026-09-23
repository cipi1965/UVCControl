using Microsoft.Extensions.DependencyInjection;
using UVCControl.App.Services;
using UVCControl.App.Services.MacOS;
using UVCControl.App.ViewModels;
using UVCControl.App.Views;

namespace UVCControl.App;

internal static class ServiceCollectionExtensions
{
    /// <summary>Registers the UI layer: services, ViewModels and windows.</summary>
    public static IServiceCollection AddUvcControlApp(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<IPresetStore, JsonPresetStore>();
        if (OperatingSystem.IsMacOS())
            services.AddSingleton<ICameraCaptureService, AVFoundationCaptureService>();
        else
            services.AddSingleton<ICameraCaptureService, FlashCapCaptureService>();

        services.AddSingleton<IViewModelFactory, ViewModelFactory>();
        services.AddSingleton<MainWindowViewModel>();

        services.AddTransient<MainWindow>();
        return services;
    }
}
