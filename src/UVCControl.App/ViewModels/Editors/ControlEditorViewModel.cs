using UVCControl.Core.Controls;
using UVCControl.Core.Devices;

namespace UVCControl.App.ViewModels.Editors;

/// <summary>Base for the part of a control row that edits its value.</summary>
public abstract class ControlEditorViewModel(ControlViewModel owner) : ViewModelBase
{
    protected ControlViewModel Owner { get; } = owner;

    protected UvcControl Control => Owner.Control;

    /// <summary>Called on the UI thread after the model changed.</summary>
    public abstract void Sync();

    public static ControlEditorViewModel Create(ControlViewModel owner) => owner.Control.Definition.Presentation switch
    {
        PresentationKind.Sliders => new SlidersEditorViewModel(owner),
        PresentationKind.Toggle => new ToggleEditorViewModel(owner),
        PresentationKind.Menu or PresentationKind.AeMode => new MenuEditorViewModel(owner),
        PresentationKind.Continuous => new ContinuousEditorViewModel(owner),
        PresentationKind.Step => new StepEditorViewModel(owner),
        _ => new RawEditorViewModel(owner),
    };
}
