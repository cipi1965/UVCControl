using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UVCControl.App.Services;
using UVCControl.App.ViewModels.Editors;
using UVCControl.Core.Devices;
using UVCControl.Core.Ptz;

namespace UVCControl.App.ViewModels;

/// <summary>The PTZ tab: joystick, hold-to-move buttons, speeds and presets for one camera.</summary>
public sealed partial class PtzViewModel : ViewModelBase, IAsyncDisposable
{
    private const string JogSpeedKey = "ptz.jogSpeed";
    private const string PresetSpeedKey = "ptz.presetSpeed";

    /// <summary>Joystick travel ignored around the centre, so a resting thumb doesn't drift the camera.</summary>
    private const double DeadZone = 0.08;

    private readonly PtzAxes _axes;
    private readonly PtzMotionController _motion;
    private readonly IPresetStore _presetStore;
    private readonly ISettingsStore _settings;
    private readonly string _cameraKey;
    private double _padPan, _padTilt, _zoomInput, _focusInput;

    public PtzViewModel(UvcDevice device, IPresetStore presetStore, ISettingsStore settings, IOptions<PtzOptions> options,
                        ILogger<PtzMotionController> logger)
    {
        _axes = PtzAxes.From(device, options.Value.SnapToResolution);
        _motion = new PtzMotionController(_axes, options.Value, logger);
        _motion.Moved += (_, _) => Dispatcher.UIThread.Post(Sync);
        _presetStore = presetStore;
        _settings = settings;
        _cameraKey = device.VidPidString;

        JogSpeed = settings.GetDouble(JogSpeedKey) ?? 50;
        PresetSpeed = settings.GetDouble(PresetSpeedKey) ?? 40;
        foreach (var preset in presetStore.Load(_cameraKey)) Presets.Add(new PresetItemViewModel(this, preset));
        SavePan = HasPan;
        SaveTilt = HasTilt;
        SaveZoom = HasZoom;
        Sync();
    }

    public bool HasPan => _axes.Pan is not null;
    public bool HasTilt => _axes.Tilt is not null;
    public bool HasPanTilt => HasPan || HasTilt;
    public bool HasZoom => _axes.Zoom is not null;
    public bool HasFocus => _axes.Focus is not null;
    public bool HasAnyAxis => !_axes.IsEmpty;
    public bool HasNoAxis => _axes.IsEmpty;

    public ObservableCollection<PresetItemViewModel> Presets { get; } = [];

    public bool HasNoPresets => Presets.Count == 0;

    // MARK: Readouts

    [ObservableProperty]
    public partial string PanText { get; private set; } = "—";

    [ObservableProperty]
    public partial string TiltText { get; private set; } = "—";

    [ObservableProperty]
    public partial string ZoomText { get; private set; } = "—";

    [ObservableProperty]
    public partial double ZoomFraction { get; private set; }

    [ObservableProperty]
    public partial string FocusText { get; private set; } = "—";

    [ObservableProperty]
    public partial bool IsMoving { get; private set; }

    // MARK: Speeds (1–100 %)

    [ObservableProperty]
    public partial double JogSpeed { get; set; }

    [ObservableProperty]
    public partial double PresetSpeed { get; set; }

    partial void OnJogSpeedChanged(double value)
    {
        _motion.JogSpeed = value / 100;
        _settings.Set(JogSpeedKey, Math.Round(value));
    }

    partial void OnPresetSpeedChanged(double value) => _settings.Set(PresetSpeedKey, Math.Round(value));

    // MARK: Joystick and buttons

    /// <summary>Joystick deflection, -1 (left) to 1 (right).</summary>
    [ObservableProperty]
    public partial double JoystickX { get; set; }

    /// <summary>Joystick deflection, -1 (down) to 1 (up).</summary>
    [ObservableProperty]
    public partial double JoystickY { get; set; }

    partial void OnJoystickXChanged(double value) => UpdateJog();

    partial void OnJoystickYChanged(double value) => UpdateJog();

    /// <summary>Starts moving in a direction given as "pan,tilt", e.g. "-1,1" for up-left.</summary>
    [RelayCommand]
    private void PressDirection(string? direction)
    {
        var parts = (direction ?? "").Split(',');
        _padPan = parts.Length > 0 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var p) ? p : 0;
        _padTilt = parts.Length > 1 && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var t) ? t : 0;
        UpdateJog();
    }

    [RelayCommand]
    private void ReleaseDirection()
    {
        _padPan = _padTilt = 0;
        UpdateJog();
    }

    /// <summary>"1" zooms in (tele), "-1" zooms out (wide).</summary>
    [RelayCommand]
    private void PressZoom(string? sign)
    {
        _zoomInput = ParseSign(sign);
        UpdateJog();
    }

    [RelayCommand]
    private void ReleaseZoom()
    {
        _zoomInput = 0;
        UpdateJog();
    }

    /// <summary>"1" focuses farther, "-1" nearer.</summary>
    [RelayCommand]
    private void PressFocus(string? sign)
    {
        _focusInput = ParseSign(sign);
        UpdateJog();
    }

    [RelayCommand]
    private void ReleaseFocus()
    {
        _focusInput = 0;
        UpdateJog();
    }

    /// <summary>Eases back to the camera's default pan, tilt and zoom.</summary>
    [RelayCommand]
    private Task HomeAsync() => _motion.RecallAsync(_axes.HomePosition(), PresetSpeed / 100);

    [RelayCommand]
    private void Stop() => _motion.Stop();

    private static double ParseSign(string? sign) =>
        double.TryParse(sign, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, -1, 1) : 0;

    private void UpdateJog()
    {
        var (x, y) = Shape(JoystickX, JoystickY);
        _motion.SetJog(Math.Clamp(x + _padPan, -1, 1), Math.Clamp(y + _padTilt, -1, 1), _zoomInput, _focusInput);
        IsMoving = _motion.IsMoving;
    }

    /// <summary>Radial dead zone, then a squared response for fine control near the centre.</summary>
    internal static (double X, double Y) Shape(double x, double y)
    {
        var magnitude = Math.Sqrt(x * x + y * y);
        if (magnitude <= DeadZone) return (0, 0);
        var scaled = Math.Min((magnitude - DeadZone) / (1 - DeadZone), 1);
        var factor = scaled * scaled / magnitude;
        return (x * factor, y * factor);
    }

    // MARK: Presets

    [ObservableProperty]
    public partial string NewPresetName { get; set; } = "";

    [ObservableProperty]
    public partial bool SavePan { get; set; }

    [ObservableProperty]
    public partial bool SaveTilt { get; set; }

    [ObservableProperty]
    public partial bool SaveZoom { get; set; }

    [ObservableProperty]
    public partial bool SaveFocus { get; set; }

    [RelayCommand]
    private void SavePreset()
    {
        if (!(SavePan && HasPan || SaveTilt && HasTilt || SaveZoom && HasZoom || SaveFocus && HasFocus)) return;
        var position = _axes.CurrentPosition();
        var name = string.IsNullOrWhiteSpace(NewPresetName) ? $"Preset {Presets.Count + 1}" : NewPresetName.Trim();
        var preset = new PtzPreset(Guid.NewGuid(), name,
            SavePan ? position.Pan : null, SaveTilt ? position.Tilt : null,
            SaveZoom ? position.Zoom : null, SaveFocus ? position.Focus : null);
        Presets.Add(new PresetItemViewModel(this, preset));
        NewPresetName = "";
        PersistPresets();
    }

    internal async Task RecallAsync(PresetItemViewModel item)
    {
        foreach (var other in Presets) other.IsRecalling = false;
        item.IsRecalling = true;
        IsMoving = true;
        await _motion.RecallAsync(item.Preset.Position, PresetSpeed / 100);
        item.IsRecalling = false;
    }

    /// <summary>Stores the current position for the axes the preset already saves.</summary>
    internal void UpdateToCurrent(PresetItemViewModel item)
    {
        var p = _axes.CurrentPosition();
        var old = item.Preset;
        item.Preset = old with
        {
            Pan = old.Pan is null ? null : p.Pan ?? old.Pan,
            Tilt = old.Tilt is null ? null : p.Tilt ?? old.Tilt,
            Zoom = old.Zoom is null ? null : p.Zoom ?? old.Zoom,
            Focus = old.Focus is null ? null : p.Focus ?? old.Focus,
        };
        PersistPresets();
    }

    internal void Rename(PresetItemViewModel item, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        item.Preset = item.Preset with { Name = name.Trim() };
        PersistPresets();
    }

    internal void Delete(PresetItemViewModel item)
    {
        Presets.Remove(item);
        PersistPresets();
    }

    internal void Move(PresetItemViewModel item, int delta)
    {
        var from = Presets.IndexOf(item);
        var to = Math.Clamp(from + delta, 0, Presets.Count - 1);
        if (from < 0 || from == to) return;
        Presets.Move(from, to);
        PersistPresets();
    }

    private void PersistPresets()
    {
        _presetStore.Save(_cameraKey, Presets.Select(p => p.Preset));
        OnPropertyChanged(nameof(HasNoPresets));
    }

    /// <summary>A short description of what a preset holds, e.g. "12.0° · -3.0° · 45 %".</summary>
    internal string Describe(PtzPreset preset)
    {
        var parts = new List<string>();
        if (preset.Pan is { } pan && _axes.Pan is { } panAxis) parts.Add("P " + Format(panAxis, pan));
        if (preset.Tilt is { } tilt && _axes.Tilt is { } tiltAxis) parts.Add("T " + Format(tiltAxis, tilt));
        if (preset.Zoom is { } zoom && _axes.Zoom is { } zoomAxis) parts.Add("Z " + Format(zoomAxis, zoom));
        if (preset.Focus is { } focus && _axes.Focus is { } focusAxis) parts.Add("F " + Format(focusAxis, focus));
        return parts.Count == 0 ? "No axes this camera supports" : string.Join(" · ", parts);
    }

    private static string Format(PtzAxis axis, long value) => axis.IsAngular
        ? string.Format(CultureInfo.InvariantCulture, "{0:0.0}°", value / PtzAxis.ArcsecondsPerDegree)
        : string.Format(CultureInfo.InvariantCulture, "{0:0} %", axis.Fraction(value) * 100);

    // MARK: Lifecycle

    /// <summary>Copies the latest positions into the readouts. Called on the UI thread.</summary>
    public void Sync()
    {
        if (_axes.Pan is { } pan) PanText = ValueFormatting.WithUnit(pan.Field.Unit, pan.Current);
        if (_axes.Tilt is { } tilt) TiltText = ValueFormatting.WithUnit(tilt.Field.Unit, tilt.Current);
        if (_axes.Zoom is { } zoom)
        {
            ZoomFraction = zoom.Fraction(zoom.Current);
            ZoomText = Format(zoom, zoom.Current);
        }
        if (_axes.Focus is { } focus) FocusText = Format(focus, focus.Current);
        IsMoving = _motion.IsMoving;
    }

    /// <summary>Stops any motion; used when the camera is no longer shown.</summary>
    public void Deactivate()
    {
        _padPan = _padTilt = _zoomInput = _focusInput = 0;
        JoystickX = JoystickY = 0;
        _motion.Stop();
    }

    public ValueTask DisposeAsync() => _motion.DisposeAsync();
}

/// <summary>One preset tile.</summary>
public sealed partial class PresetItemViewModel(PtzViewModel owner, PtzPreset preset) : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Summary))]
    public partial PtzPreset Preset { get; set; } = preset;

    public string Name => Preset.Name;

    public string Summary => owner.Describe(Preset);

    [ObservableProperty]
    public partial bool IsRecalling { get; set; }

    [ObservableProperty]
    public partial bool IsRenaming { get; private set; }

    [ObservableProperty]
    public partial string EditName { get; set; } = "";

    [RelayCommand]
    private Task RecallAsync() => owner.RecallAsync(this);

    [RelayCommand]
    private void UpdateToCurrent() => owner.UpdateToCurrent(this);

    [RelayCommand]
    private void BeginRename()
    {
        EditName = Name;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CommitRename()
    {
        if (!IsRenaming) return;
        IsRenaming = false;
        owner.Rename(this, EditName);
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    [RelayCommand]
    private void MoveLeft() => owner.Move(this, -1);

    [RelayCommand]
    private void MoveRight() => owner.Move(this, 1);

    [RelayCommand]
    private void Delete() => owner.Delete(this);
}
