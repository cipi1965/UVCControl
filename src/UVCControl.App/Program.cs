using Avalonia;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UVCControl.App.Services;
using UVCControl.Core;
using UVCControl.Core.Devices;
using UVCControl.Core.Ptz;

namespace UVCControl.App;

internal static class Program
{
    // Don't use any Avalonia, third-party APIs or any SynchronizationContext-reliant code before
    // StartWithClassicDesktopLifetime is called: things aren't initialized yet.
    [STAThread]
    public static int Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Services
            .AddUvcControl()
            .Configure<UvcOptions>(builder.Configuration.GetSection(UvcOptions.SectionName))
            .Configure<PtzOptions>(builder.Configuration.GetSection(PtzOptions.SectionName));
        builder.Services.AddUvcControlApp();

        using var host = builder.Build();

        // `UVCControl --dump` prints every discovered control, handy for checking a new camera.
        if (args.Contains("--dump"))
            return DeviceDump.RunAsync(host.Services.GetRequiredService<IUvcDeviceManager>(), Console.Out)
                .GetAwaiter().GetResult();

        host.Start();
        try
        {
            return BuildAvaloniaApp(host.Services).StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(null);

    private static AppBuilder BuildAvaloniaApp(IServiceProvider? services) =>
        AppBuilder.Configure(() => new App(services))
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
