using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace UVCControl.App.Controls;

/// <summary>
/// Attached <c>IsEditing</c> flag that is true while the pointer drags a <see cref="Slider"/>.
/// Bind it two-way to keep periodic refreshes from fighting the user's drag.
/// </summary>
public static class SliderEditTracker
{
    public static readonly AttachedProperty<bool> IsEditingProperty =
        AvaloniaProperty.RegisterAttached<Slider, bool>("IsEditing", typeof(SliderEditTracker));

    static SliderEditTracker()
    {
        const RoutingStrategies routes = RoutingStrategies.Tunnel | RoutingStrategies.Bubble;
        InputElement.PointerPressedEvent.AddClassHandler<Slider>((s, _) => s.SetCurrentValue(IsEditingProperty, true), routes, true);
        InputElement.PointerReleasedEvent.AddClassHandler<Slider>((s, _) => s.SetCurrentValue(IsEditingProperty, false), routes, true);
        InputElement.PointerCaptureLostEvent.AddClassHandler<Slider>((s, _) => s.SetCurrentValue(IsEditingProperty, false), routes, true);
    }

    public static bool GetIsEditing(Slider slider) => slider.GetValue(IsEditingProperty);

    public static void SetIsEditing(Slider slider, bool value) => slider.SetValue(IsEditingProperty, value);
}
