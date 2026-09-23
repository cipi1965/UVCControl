namespace UVCControl.Core.Ptz;

public static class Easing
{
    /// <summary>Cubic ease-in/out: starts and ends at zero speed, peaks at 1.5× the average speed halfway.</summary>
    public static double InOutCubic(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }

    /// <summary>Peak speed of <see cref="InOutCubic"/> relative to its average speed.</summary>
    public const double InOutCubicPeak = 1.5;
}
