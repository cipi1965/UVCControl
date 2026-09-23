using System.Globalization;
using UVCControl.Core.Controls;

namespace UVCControl.App.ViewModels.Editors;

internal static class ValueFormatting
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>Compact label for the ends of a slider.</summary>
    public static string Range(DisplayUnit unit, long v) => unit switch
    {
        DisplayUnit.Arcseconds => string.Format(Culture, "{0:0}°", v / 3600.0),
        DisplayUnit.HundredMicroseconds => string.Format(Culture, "{0:0.0} ms", v / 10.0),
        DisplayUnit.Degrees => $"{v}°",
        DisplayUnit.Kelvin => $"{v} K",
        _ => v.ToString(Culture),
    };

    /// <summary>Human-readable version of the raw value, shown next to the text field.</summary>
    public static string WithUnit(DisplayUnit unit, long v) => unit switch
    {
        DisplayUnit.Arcseconds => string.Format(Culture, "{0:0.00}°", v / 3600.0),
        DisplayUnit.HundredMicroseconds => v >= 10
            ? string.Format(Culture, "{0:0.0} ms", v / 10.0)
            : string.Format(Culture, "1/{0:0} s", 10_000.0 / Math.Max(v, 1)),
        DisplayUnit.Degrees => $"{v}°",
        DisplayUnit.Kelvin => $"{v} K",
        _ => "",
    };
}
