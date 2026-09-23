using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UVCControl.Core.Devices;
using UVCControl.Core.Platform.Linux;
using UVCControl.Core.Platform.MacOS;
using UVCControl.Core.Platform.Windows;
using UVCControl.Core.Ptz;
using UVCControl.Core.Transport;

namespace UVCControl.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the UVC device manager, the backend for the current operating system and PTZ options.</summary>
    public static IServiceCollection AddUvcControl(this IServiceCollection services)
    {
        services.AddOptions<UvcOptions>();
        services.AddOptions<PtzOptions>();
        services.TryAddSingleton<IUvcBackend>(_ => CreatePlatformBackend());
        services.TryAddSingleton<IUvcDeviceManager, UvcDeviceManager>();
        return services;
    }

    private static IUvcBackend CreatePlatformBackend()
    {
        if (OperatingSystem.IsMacOS()) return new MacUvcBackend();
        if (OperatingSystem.IsLinux()) return new LinuxUvcBackend();
        if (OperatingSystem.IsWindows()) return new WindowsUvcBackend();
        return new UnsupportedUvcBackend();
    }
}
