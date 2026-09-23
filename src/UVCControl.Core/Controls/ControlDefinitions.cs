namespace UVCControl.Core.Controls;

/// <summary>How a single field inside a control's payload behaves.</summary>
public enum FieldRole
{
    /// <summary>An absolute value, edited with a slider.</summary>
    Value,
    /// <summary>A signed direction (-1, 0, +1) for relative/continuous movement.</summary>
    Direction,
    /// <summary>A speed that accompanies a direction.</summary>
    Speed,
    /// <summary>An on/off flag inside a composite control.</summary>
    Flag,
}

/// <summary>How a value is shown to the user.</summary>
public enum DisplayUnit
{
    None,
    /// <summary>Shown as degrees.</summary>
    Arcseconds,
    /// <summary>Shown as milliseconds.</summary>
    HundredMicroseconds,
    Degrees,
    Kelvin,
}

public sealed record ControlField(
    string Name,
    int Offset,
    int Size,
    bool Signed = false,
    FieldRole Role = FieldRole.Value,
    DisplayUnit Unit = DisplayUnit.None);

public sealed record MenuOption(int Value, string Label);

public enum PresentationKind
{
    /// <summary>One slider per <see cref="FieldRole.Value"/> field.</summary>
    Sliders,
    Toggle,
    Menu,
    /// <summary>CT_AE_MODE: a menu whose valid options come from the GET_RES bitmap.</summary>
    AeMode,
    /// <summary>Hold-to-move buttons; direction goes back to 0 on release.</summary>
    Continuous,
    /// <summary>Single-shot step buttons (+1 / -1).</summary>
    Step,
    /// <summary>Unknown layout (extension units): hex editor.</summary>
    Raw,
}

public sealed record ControlDefinition(
    byte Selector,
    string Name,
    int Length,
    PresentationKind Presentation,
    IReadOnlyList<ControlField> Fields,
    IReadOnlyList<MenuOption>? MenuOptions = null)
{
    /// <summary>Absolute controls have a meaningful default to reset to.</summary>
    public bool IsAbsolute => Presentation is not (PresentationKind.Continuous or PresentationKind.Step or PresentationKind.Raw);

    internal static ControlDefinition Scalar(byte selector, string name, int size, bool signed = false,
                                             DisplayUnit unit = DisplayUnit.None) =>
        new(selector, name, size, PresentationKind.Sliders, [new ControlField(name, 0, size, signed, Unit: unit)]);

    internal static ControlDefinition Toggle(byte selector, string name) =>
        new(selector, name, 1, PresentationKind.Toggle, [new ControlField(name, 0, 1)]);

    internal static ControlDefinition Menu(byte selector, string name, string fieldName, params MenuOption[] options) =>
        new(selector, name, 1, PresentationKind.Menu, [new ControlField(fieldName, 0, 1)], options);

    public static ControlDefinition Raw(byte selector, int length) =>
        new(selector, $"Control {selector}", length, PresentationKind.Raw, []);
}

public static class UvcControlCatalog
{
    /// <summary>UVC 1.5 Camera Terminal controls (spec section 4.2.2.1).</summary>
    public static readonly IReadOnlyList<ControlDefinition> CameraTerminal =
    [
        ControlDefinition.Toggle(0x01, "Scanning Mode (interlaced)"),
        new(0x02, "Auto-Exposure Mode", 1, PresentationKind.AeMode, [new ControlField("Mode", 0, 1)]),
        ControlDefinition.Toggle(0x03, "Auto-Exposure Priority"),
        ControlDefinition.Scalar(0x04, "Exposure Time", 4, unit: DisplayUnit.HundredMicroseconds),
        new(0x05, "Exposure Time (Relative)", 1, PresentationKind.Step,
            [new ControlField("Exposure", 0, 1, Signed: true, Role: FieldRole.Direction)]),
        ControlDefinition.Scalar(0x06, "Focus", 2),
        new(0x07, "Focus (Relative)", 2, PresentationKind.Continuous,
        [
            new ControlField("Focus", 0, 1, Signed: true, Role: FieldRole.Direction),
            new ControlField("Speed", 1, 1, Role: FieldRole.Speed),
        ]),
        ControlDefinition.Toggle(0x08, "Autofocus"),
        ControlDefinition.Scalar(0x09, "Iris", 2),
        new(0x0A, "Iris (Relative)", 1, PresentationKind.Step,
            [new ControlField("Iris", 0, 1, Signed: true, Role: FieldRole.Direction)]),
        ControlDefinition.Scalar(0x0B, "Zoom", 2),
        new(0x0C, "Zoom (Relative)", 3, PresentationKind.Continuous,
        [
            new ControlField("Zoom", 0, 1, Signed: true, Role: FieldRole.Direction),
            new ControlField("Digital Zoom", 1, 1, Role: FieldRole.Flag),
            new ControlField("Speed", 2, 1, Role: FieldRole.Speed),
        ]),
        new(0x0D, "Pan / Tilt", 8, PresentationKind.Sliders,
        [
            new ControlField("Pan", 0, 4, Signed: true, Unit: DisplayUnit.Arcseconds),
            new ControlField("Tilt", 4, 4, Signed: true, Unit: DisplayUnit.Arcseconds),
        ]),
        new(0x0E, "Pan / Tilt (Relative)", 4, PresentationKind.Continuous,
        [
            new ControlField("Pan", 0, 1, Signed: true, Role: FieldRole.Direction),
            new ControlField("Pan Speed", 1, 1, Role: FieldRole.Speed),
            new ControlField("Tilt", 2, 1, Signed: true, Role: FieldRole.Direction),
            new ControlField("Tilt Speed", 3, 1, Role: FieldRole.Speed),
        ]),
        ControlDefinition.Scalar(0x0F, "Roll", 2, signed: true, unit: DisplayUnit.Degrees),
        new(0x10, "Roll (Relative)", 2, PresentationKind.Continuous,
        [
            new ControlField("Roll", 0, 1, Signed: true, Role: FieldRole.Direction),
            new ControlField("Speed", 1, 1, Role: FieldRole.Speed),
        ]),
        ControlDefinition.Toggle(0x11, "Privacy Shutter"),
        ControlDefinition.Menu(0x12, "Focus (Simple)", "Focus",
            new(0, "Full range"), new(1, "Macro"), new(2, "People"), new(3, "Scene")),
        new(0x13, "Digital Window", 12, PresentationKind.Sliders,
        [
            new ControlField("Top", 0, 2), new ControlField("Left", 2, 2),
            new ControlField("Bottom", 4, 2), new ControlField("Right", 6, 2),
            new ControlField("Steps", 8, 2), new ControlField("Steps Units", 10, 2),
        ]),
        new(0x14, "Region of Interest", 10, PresentationKind.Sliders,
        [
            new ControlField("Top", 0, 2), new ControlField("Left", 2, 2),
            new ControlField("Bottom", 4, 2), new ControlField("Right", 6, 2),
            new ControlField("Auto Controls", 8, 2),
        ]),
    ];

    /// <summary>UVC 1.5 Processing Unit controls (spec section 4.2.2.3).</summary>
    public static readonly IReadOnlyList<ControlDefinition> ProcessingUnit =
    [
        ControlDefinition.Scalar(0x01, "Backlight Compensation", 2),
        ControlDefinition.Scalar(0x02, "Brightness", 2, signed: true),
        ControlDefinition.Scalar(0x03, "Contrast", 2),
        ControlDefinition.Scalar(0x04, "Gain", 2),
        ControlDefinition.Menu(0x05, "Power Line Frequency", "Frequency",
            new(0, "Disabled"), new(1, "50 Hz"), new(2, "60 Hz"), new(3, "Auto")),
        ControlDefinition.Scalar(0x06, "Hue", 2, signed: true),
        ControlDefinition.Scalar(0x07, "Saturation", 2),
        ControlDefinition.Scalar(0x08, "Sharpness", 2),
        ControlDefinition.Scalar(0x09, "Gamma", 2),
        ControlDefinition.Scalar(0x0A, "White Balance Temperature", 2, unit: DisplayUnit.Kelvin),
        ControlDefinition.Toggle(0x0B, "Auto White Balance Temperature"),
        new(0x0C, "White Balance Component", 4, PresentationKind.Sliders,
            [new ControlField("Blue", 0, 2), new ControlField("Red", 2, 2)]),
        ControlDefinition.Toggle(0x0D, "Auto White Balance Component"),
        ControlDefinition.Scalar(0x0E, "Digital Multiplier", 2),
        ControlDefinition.Scalar(0x0F, "Digital Multiplier Limit", 2),
        ControlDefinition.Toggle(0x10, "Auto Hue"),
        ControlDefinition.Scalar(0x11, "Analog Video Standard", 1),
        ControlDefinition.Scalar(0x12, "Analog Lock Status", 1),
        ControlDefinition.Toggle(0x13, "Auto Contrast"),
    ];

    public static readonly IReadOnlyList<MenuOption> AeModeOptions =
    [
        new(0x01, "Manual"),
        new(0x02, "Auto"),
        new(0x04, "Shutter Priority"),
        new(0x08, "Aperture Priority"),
    ];

    /// <summary>bmControls bit index for each Camera Terminal selector (the bitmap order differs from selector order).</summary>
    public static readonly IReadOnlyDictionary<byte, int> CameraTerminalBits = new Dictionary<byte, int>
    {
        [0x01] = 0, [0x02] = 1, [0x03] = 2, [0x04] = 3, [0x05] = 4, [0x06] = 5, [0x07] = 6, [0x09] = 7, [0x0A] = 8,
        [0x0B] = 9, [0x0C] = 10, [0x0D] = 11, [0x0E] = 12, [0x0F] = 13, [0x10] = 14, [0x08] = 17, [0x11] = 18,
        [0x12] = 19, [0x13] = 20, [0x14] = 21,
    };

    /// <summary>bmControls bit index for each Processing Unit selector.</summary>
    public static readonly IReadOnlyDictionary<byte, int> ProcessingUnitBits = new Dictionary<byte, int>
    {
        [0x02] = 0, [0x03] = 1, [0x06] = 2, [0x07] = 3, [0x08] = 4, [0x09] = 5, [0x0A] = 6, [0x0C] = 7, [0x01] = 8,
        [0x04] = 9, [0x05] = 10, [0x10] = 11, [0x0B] = 12, [0x0D] = 13, [0x0E] = 14, [0x0F] = 15, [0x11] = 16,
        [0x12] = 17, [0x13] = 18,
    };

    /// <summary>Returns true when <paramref name="bit"/> is set in a UVC bmControls bitmap.</summary>
    public static bool IsAdvertised(ReadOnlySpan<byte> bmControls, int? bit) =>
        bit is { } b && b >= 0 && b / 8 < bmControls.Length && (bmControls[b / 8] & (1 << (b % 8))) != 0;
}
