using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace UVCControl.App.Controls;

/// <summary>
/// A round thumb-stick. Drag the knob to set <see cref="X"/> (-1 left … 1 right) and <see cref="Y"/>
/// (-1 down … 1 up), clamped to the unit circle; it springs back to the centre when released.
/// </summary>
public class VirtualJoystick : Control
{
    public static readonly StyledProperty<double> XProperty =
        AvaloniaProperty.Register<VirtualJoystick, double>(nameof(X), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> YProperty =
        AvaloniaProperty.Register<VirtualJoystick, double>(nameof(Y), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<VirtualJoystick, IBrush?>(nameof(Background), Brushes.Transparent);

    public static readonly StyledProperty<IBrush?> BorderBrushProperty =
        AvaloniaProperty.Register<VirtualJoystick, IBrush?>(nameof(BorderBrush), Brushes.Gray);

    public static readonly StyledProperty<IBrush?> KnobBrushProperty =
        AvaloniaProperty.Register<VirtualJoystick, IBrush?>(nameof(KnobBrush), Brushes.DodgerBlue);

    private bool _dragging;

    static VirtualJoystick()
    {
        AffectsRender<VirtualJoystick>(XProperty, YProperty, BackgroundProperty, BorderBrushProperty, KnobBrushProperty,
                                       IsEnabledProperty);
    }

    public double X
    {
        get => GetValue(XProperty);
        set => SetValue(XProperty, value);
    }

    public double Y
    {
        get => GetValue(YProperty);
        set => SetValue(YProperty, value);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public IBrush? BorderBrush
    {
        get => GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    public IBrush? KnobBrush
    {
        get => GetValue(KnobBrushProperty);
        set => SetValue(KnobBrushProperty, value);
    }

    private Point Center => new(Bounds.Width / 2, Bounds.Height / 2);
    private double Radius => Math.Max(Math.Min(Bounds.Width, Bounds.Height) / 2 - 1, 1);
    private double KnobRadius => Radius * 0.3;
    private double Travel => Math.Max(Radius - KnobRadius, 1);

    public override void Render(DrawingContext context)
    {
        var center = Center;
        var radius = Radius;
        var pen = new Pen(BorderBrush, 1);
        context.DrawEllipse(Background, pen, center, radius, radius);
        context.DrawLine(pen, center - new Vector(radius * 0.6, 0), center + new Vector(radius * 0.6, 0));
        context.DrawLine(pen, center - new Vector(0, radius * 0.6), center + new Vector(0, radius * 0.6));
        context.DrawEllipse(null, pen, center, Travel * 0.5, Travel * 0.5);

        var knob = center + new Vector(X * Travel, -Y * Travel);
        using (context.PushOpacity(IsEffectivelyEnabled ? 1 : 0.4))
            context.DrawEllipse(KnobBrush, null, knob, KnobRadius, KnobRadius);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEffectivelyEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        e.Pointer.Capture(this);
        MoveKnob(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) MoveKnob(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        e.Pointer.Capture(null);
        Release();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        Release();
    }

    private void MoveKnob(Point point)
    {
        var offset = (point - Center) / Travel;
        var length = Math.Sqrt(offset.X * offset.X + offset.Y * offset.Y);
        if (length > 1) offset /= length;
        SetCurrentValue(XProperty, offset.X);
        SetCurrentValue(YProperty, -offset.Y);
    }

    private void Release()
    {
        if (!_dragging) return;
        _dragging = false;
        SetCurrentValue(XProperty, 0.0);
        SetCurrentValue(YProperty, 0.0);
    }
}
