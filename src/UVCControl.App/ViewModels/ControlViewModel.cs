using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UVCControl.App.ViewModels.Editors;
using UVCControl.Core.Devices;

namespace UVCControl.App.ViewModels;

/// <summary>One control row: name, status badges, a reset button and a presentation-specific editor.</summary>
public sealed partial class ControlViewModel : ViewModelBase
{
    public ControlViewModel(UvcControl control)
    {
        Control = control;
        Editor = ControlEditorViewModel.Create(this);
        Sync();
    }

    public UvcControl Control { get; }

    public ControlEditorViewModel Editor { get; }

    public string Name => Control.Definition.Name;

    /// <summary>The device descriptor doesn't list this control, but the camera answers it.</summary>
    public bool IsNotAdvertised => !Control.Advertised;

    [ObservableProperty]
    public partial bool IsAuto { get; private set; }

    [ObservableProperty]
    public partial bool IsReadOnly { get; private set; }

    [ObservableProperty]
    public partial bool IsEditable { get; private set; }

    [ObservableProperty]
    public partial bool CanReset { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [RelayCommand]
    private Task ResetAsync() => RunAsync(Control.ResetToDefaultAsync);

    /// <summary>Runs a device operation, then refreshes this row from the model.</summary>
    public async Task RunAsync(Func<Task> operation)
    {
        await operation();
        Sync();
    }

    /// <summary>Copies the model's latest state into the bindable properties.</summary>
    public void Sync()
    {
        IsAuto = Control.DisabledByAutoMode;
        IsReadOnly = !Control.SupportsSet;
        IsEditable = Control.SupportsSet && !Control.DisabledByAutoMode;
        CanReset = Control.SupportsSet && Control.DefaultValue is not null && Control.Definition.IsAbsolute;
        Error = Control.LastError;
        Editor.Sync();
    }
}
