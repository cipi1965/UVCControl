using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace UVCControl.App.Controls;

/// <summary>
/// A button that runs <see cref="PressCommand"/> when pressed and <see cref="ReleaseCommand"/> when let go.
/// Both get <see cref="Button.CommandParameter"/>.
/// </summary>
public class HoldButton : Button
{
    public static readonly StyledProperty<ICommand?> PressCommandProperty =
        AvaloniaProperty.Register<HoldButton, ICommand?>(nameof(PressCommand));

    public static readonly StyledProperty<ICommand?> ReleaseCommandProperty =
        AvaloniaProperty.Register<HoldButton, ICommand?>(nameof(ReleaseCommand));

    private bool _held;

    protected override Type StyleKeyOverride => typeof(Button);

    public ICommand? PressCommand
    {
        get => GetValue(PressCommandProperty);
        set => SetValue(PressCommandProperty, value);
    }

    public ICommand? ReleaseCommand
    {
        get => GetValue(ReleaseCommandProperty);
        set => SetValue(ReleaseCommandProperty, value);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_held || !IsEffectivelyEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _held = true;
        PressCommand?.Execute(CommandParameter);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        Release();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        Release();
    }

    private void Release()
    {
        if (!_held) return;
        _held = false;
        ReleaseCommand?.Execute(CommandParameter);
    }
}
