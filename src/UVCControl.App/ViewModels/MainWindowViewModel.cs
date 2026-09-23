using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UVCControl.Core.Devices;

namespace UVCControl.App.ViewModels;

public sealed partial class MainWindowViewModel(IUvcDeviceManager manager, IViewModelFactory factory) : ViewModelBase
{
    public ObservableCollection<DeviceViewModel> Devices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial DeviceViewModel? SelectedDevice { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoDevices))]
    public partial bool IsScanning { get; set; }

    public bool HasSelection => SelectedDevice is not null;

    public bool ShowNoDevices => Devices.Count == 0 && !IsScanning;

    public string BackendName => manager.BackendName;

    [RelayCommand]
    private async Task RescanAsync()
    {
        IsScanning = true;
        try
        {
            var found = await manager.ReloadAsync();

            foreach (var gone in Devices.Where(vm => !found.Contains(vm.Device)).ToList())
            {
                Devices.Remove(gone);
                gone.Dispose();
            }
            foreach (var (device, index) in found.Select((d, i) => (d, i)))
            {
                if (Devices.All(vm => vm.Device != device)) Devices.Insert(index, factory.CreateDevice(device));
            }

            // Connect every camera up front, so switching between them is instant.
            foreach (var vm in Devices) _ = vm.PrepareAsync();

            if (SelectedDevice is null || !Devices.Contains(SelectedDevice)) SelectedDevice = Devices.FirstOrDefault();
        }
        finally
        {
            IsScanning = false;
            OnPropertyChanged(nameof(ShowNoDevices));
        }
    }

    partial void OnSelectedDeviceChanged(DeviceViewModel? oldValue, DeviceViewModel? newValue)
    {
        oldValue?.Deactivate();
        _ = newValue?.ActivateAsync();
    }
}
