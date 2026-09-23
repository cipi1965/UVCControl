using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UVCControl.Core;
using UVCControl.Core.Controls;

namespace UVCControl.App.ViewModels.Editors;

/// <summary>Pan/tilt, roll, zoom and focus relative controls: move while a button is held.</summary>
public sealed class ContinuousEditorViewModel : ControlEditorViewModel
{
    public ContinuousEditorViewModel(ControlViewModel owner) : base(owner)
    {
        var fields = Control.Definition.Fields;
        Axes = fields.Where(f => f.Role == FieldRole.Direction).Select(direction =>
        {
            // The speed field immediately follows its direction field.
            var speed = fields.FirstOrDefault(f => f.Role == FieldRole.Speed && f.Offset == direction.Offset + direction.Size)
                        ?? fields.FirstOrDefault(f => f.Role == FieldRole.Speed);
            return new AxisViewModel(this, direction, speed is null ? null : new SpeedViewModel(speed));
        }).ToList();
        Flags = fields.Where(f => f.Role == FieldRole.Flag).Select(f => new FlagViewModel(f)).ToList();
    }

    public IReadOnlyList<AxisViewModel> Axes { get; }

    public IReadOnlyList<FlagViewModel> Flags { get; }

    public override void Sync()
    {
        foreach (var axis in Axes) axis.Speed?.Sync(Control);
    }

    internal Task MoveAsync(ControlField direction, int sign)
    {
        var payload = new byte[Control.Length];
        foreach (var f in Control.Definition.Fields)
        {
            var value = f.Role switch
            {
                FieldRole.Direction => f.Offset == direction.Offset ? sign : 0,
                FieldRole.Speed => Axes.Select(a => a.Speed).FirstOrDefault(s => s?.Field == f)?.Speed ?? 1,
                FieldRole.Flag => Flags.First(flag => flag.Field == f).IsOn ? 1 : 0,
                _ => (long?)null,
            };
            if (value is { } v && f.Offset + f.Size <= payload.Length) payload.WriteInt(v, f.Offset, f.Size);
        }
        return Owner.RunAsync(() => Control.SendAsync(payload, updateCurrent: false));
    }
}

public sealed partial class AxisViewModel(ContinuousEditorViewModel editor, ControlField direction, SpeedViewModel? speed)
    : ViewModelBase
{
    public string Name => direction.Name;

    public SpeedViewModel? Speed => speed;

    [RelayCommand]
    private Task PressMinusAsync() => editor.MoveAsync(direction, -1);

    [RelayCommand]
    private Task PressPlusAsync() => editor.MoveAsync(direction, 1);

    [RelayCommand]
    private Task ReleaseAsync() => editor.MoveAsync(direction, 0);
}

public sealed partial class SpeedViewModel(ControlField controlField) : ViewModelBase
{
    private bool _initialized;

    public ControlField Field => controlField;

    [ObservableProperty]
    public partial double Minimum { get; private set; } = 1;

    [ObservableProperty]
    public partial double Maximum { get; private set; } = 1;

    [ObservableProperty]
    public partial bool HasRange { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedText))]
    public partial double Value { get; set; } = 1;

    public int Speed => (int)Math.Round(Value);

    public string SpeedText => HasRange ? Speed.ToString() : $"Speed {Speed}";

    public void Sync(Core.Devices.UvcControl control)
    {
        var a = control.MinValue(controlField) ?? 1;
        var b = control.MaxValue(controlField) ?? 1;
        Minimum = Math.Max(Math.Min(a, b), 1);
        Maximum = Math.Max(Math.Max(a, b), 1);
        HasRange = Minimum < Maximum;
        if (!_initialized)
        {
            Value = Maximum;
            _initialized = true;
        }
        OnPropertyChanged(nameof(SpeedText));
    }
}

public sealed partial class FlagViewModel(ControlField controlField) : ViewModelBase
{
    public ControlField Field => controlField;

    public string Name => controlField.Name;

    [ObservableProperty]
    public partial bool IsOn { get; set; }
}
