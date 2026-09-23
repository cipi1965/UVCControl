using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UVCControl.Core.Controls;

namespace UVCControl.App.ViewModels.Editors;

public sealed partial class ToggleEditorViewModel(ControlViewModel owner) : ControlEditorViewModel(owner)
{
    private bool _syncing;

    private ControlField Field => Control.Definition.Fields[0];

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    public override void Sync()
    {
        _syncing = true;
        IsOn = Control.Value(Field) != 0;
        _syncing = false;
    }

    partial void OnIsOnChanged(bool value)
    {
        if (!_syncing) _ = Owner.RunAsync(() => Control.SetAsync(Field, value ? 1 : 0));
    }
}

/// <summary>A fixed list of values; for CT_AE_MODE the list is filtered by the GET_RES bitmap.</summary>
public sealed partial class MenuEditorViewModel(ControlViewModel owner) : ControlEditorViewModel(owner)
{
    private bool _syncing;

    private ControlField Field => Control.Definition.Fields[0];

    public ObservableCollection<MenuOption> Options { get; } = [];

    [ObservableProperty]
    public partial MenuOption? Selected { get; set; }

    public override void Sync()
    {
        _syncing = true;
        var value = (int)Control.Value(Field);
        var options = AvailableOptions().ToList();
        if (options.All(o => o.Value != value)) options.Add(new MenuOption(value, $"Unknown ({value})"));
        if (!options.SequenceEqual(Options))
        {
            Options.Clear();
            foreach (var option in options) Options.Add(option);
        }
        Selected = Options.FirstOrDefault(o => o.Value == value);
        _syncing = false;
    }

    private IEnumerable<MenuOption> AvailableOptions()
    {
        if (Control.Definition.Presentation != PresentationKind.AeMode) return Control.Definition.MenuOptions ?? [];
        if (Control.Resolution is not { Length: > 0 } res) return UvcControlCatalog.AeModeOptions;
        var supported = UvcControlCatalog.AeModeOptions.Where(o => (res[0] & o.Value) != 0).ToList();
        return supported.Count > 0 ? supported : UvcControlCatalog.AeModeOptions;
    }

    partial void OnSelectedChanged(MenuOption? value)
    {
        if (!_syncing && value is not null) _ = Owner.RunAsync(() => Control.SetAsync(Field, value.Value));
    }
}

/// <summary>Exposure/iris relative: each click steps once.</summary>
public sealed partial class StepEditorViewModel(ControlViewModel owner) : ControlEditorViewModel(owner)
{
    public override void Sync() { }

    [RelayCommand]
    private Task DecreaseAsync() => Send(0xFF);

    [RelayCommand]
    private Task DefaultAsync() => Send(0x00);

    [RelayCommand]
    private Task IncreaseAsync() => Send(0x01);

    private Task Send(byte step) => Owner.RunAsync(() => Control.SendAsync([step], updateCurrent: false));
}
