using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UVCControl.Core.Controls;

namespace UVCControl.App.ViewModels.Editors;

/// <summary>One slider per value field.</summary>
public sealed class SlidersEditorViewModel : ControlEditorViewModel
{
    public SlidersEditorViewModel(ControlViewModel owner) : base(owner)
    {
        var fields = Control.Definition.Fields;
        Fields = fields.Select(f => new FieldSliderViewModel(owner, f, showLabel: fields.Count > 1)).ToList();
    }

    public IReadOnlyList<FieldSliderViewModel> Fields { get; }

    public override void Sync()
    {
        foreach (var slider in Fields) slider.Sync();
    }
}

public sealed partial class FieldSliderViewModel(ControlViewModel owner, ControlField controlField, bool showLabel) : ViewModelBase
{
    private long _value;
    private long _lo, _hi, _step = 1;
    private string _syncedText = "";
    private bool _syncing;

    public string Name => controlField.Name;
    public bool ShowLabel => showLabel;

    [ObservableProperty]
    public partial double Minimum { get; private set; }

    [ObservableProperty]
    public partial double Maximum { get; private set; }

    [ObservableProperty]
    public partial bool HasRange { get; private set; }

    [ObservableProperty]
    public partial string MinimumLabel { get; private set; } = "";

    [ObservableProperty]
    public partial string MaximumLabel { get; private set; } = "";

    [ObservableProperty]
    public partial double SliderValue { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    public partial string UnitText { get; private set; } = "";

    /// <summary>True while the user drags the slider; refreshes then leave the value alone.</summary>
    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    public void Sync()
    {
        var control = owner.Control;
        _lo = control.MinValue(controlField) ?? 0;
        _hi = control.MaxValue(controlField) ?? 0;
        _step = Math.Max(control.ResValue(controlField) ?? 1, 1);

        _syncing = true;
        Minimum = _lo;
        Maximum = _hi;
        HasRange = _lo < _hi;
        MinimumLabel = ValueFormatting.Range(controlField.Unit, _lo);
        MaximumLabel = ValueFormatting.Range(controlField.Unit, _hi);
        if (!IsEditing)
        {
            _value = control.Value(controlField);
            SliderValue = _value;
        }
        // Leave the text box alone while the user is typing in it.
        if (Text == _syncedText)
        {
            Text = _syncedText = _value.ToString(CultureInfo.InvariantCulture);
        }
        UnitText = ValueFormatting.WithUnit(controlField.Unit, _value);
        _syncing = false;
    }

    partial void OnSliderValueChanged(double value)
    {
        if (_syncing) return;
        // Snap to the device's resolution, anchored at the minimum.
        var snapped = Math.Clamp(_lo + (long)Math.Round((value - _lo) / _step) * _step, _lo, _hi);
        if (snapped == _value) return;
        _value = snapped;
        UnitText = ValueFormatting.WithUnit(controlField.Unit, snapped);
        if (Text == _syncedText) Text = _syncedText = snapped.ToString(CultureInfo.InvariantCulture);
        _ = owner.RunAsync(() => owner.Control.SetAsync(controlField, snapped));
    }

    partial void OnIsEditingChanged(bool value) => owner.Control.IsEditing = value;

    [RelayCommand]
    private async Task CommitTextAsync()
    {
        if (long.TryParse(Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            await owner.RunAsync(() => owner.Control.SetAsync(controlField, v));
        _syncedText = Text = owner.Control.Value(controlField).ToString(CultureInfo.InvariantCulture);
        Sync();
    }
}
