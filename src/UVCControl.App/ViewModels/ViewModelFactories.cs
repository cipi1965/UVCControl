using Microsoft.Extensions.DependencyInjection;
using UVCControl.Core.Devices;
using UVCControl.Core.Transport;

namespace UVCControl.App.ViewModels;

/// <summary>Creates ViewModels that combine DI services with a runtime argument.</summary>
public interface IViewModelFactory
{
    DeviceViewModel CreateDevice(UvcDevice device);

    CameraPreviewViewModel CreatePreview(UvcDeviceInfo device);

    PtzViewModel CreatePtz(UvcDevice device);
}

internal sealed class ViewModelFactory(IServiceProvider services) : IViewModelFactory
{
    public DeviceViewModel CreateDevice(UvcDevice device) =>
        ActivatorUtilities.CreateInstance<DeviceViewModel>(services, device);

    public CameraPreviewViewModel CreatePreview(UvcDeviceInfo device) =>
        ActivatorUtilities.CreateInstance<CameraPreviewViewModel>(services, device);

    public PtzViewModel CreatePtz(UvcDevice device) =>
        ActivatorUtilities.CreateInstance<PtzViewModel>(services, device);
}
