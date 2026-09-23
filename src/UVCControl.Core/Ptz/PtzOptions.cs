namespace UVCControl.Core.Ptz;

public sealed class PtzOptions
{
    public const string SectionName = "Ptz";

    /// <summary>How often a moving camera gets a new absolute position.</summary>
    public double TickRate { get; set; } = 30;

    /// <summary>Pan/tilt speed at 100 %, in degrees per second.</summary>
    public double MaxPanTiltDegreesPerSecond { get; set; } = 90;

    /// <summary>Zoom speed at 100 %, as a fraction of the full range per second.</summary>
    public double MaxZoomRangePerSecond { get; set; } = 0.6;

    /// <summary>Focus speed at 100 %, as a fraction of the full range per second.</summary>
    public double MaxFocusRangePerSecond { get; set; } = 0.6;

    /// <summary>
    /// Round streamed positions to the step the camera reports (GET_RES). Off by default: cameras such as the
    /// DJI Osmo Pocket 3 report 1° for pan/tilt but hold finer positions, and 1° steps make slow moves stutter.
    /// </summary>
    public bool SnapToResolution { get; set; }

    /// <summary>Shortest preset move, so tiny corrections still ease instead of jumping.</summary>
    public TimeSpan MinimumMoveDuration { get; set; } = TimeSpan.FromMilliseconds(250);
}
