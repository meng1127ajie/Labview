namespace V3RttMonitor.Core.Visualization;

public static class WheelZoom
{
    private const double FactorPerDetent = 1.20;

    /// <summary>
    /// WPF reports +120 for one upward wheel detent. ScottPlot zoom factors
    /// greater than one zoom in, so positive deltas must return factors > 1.
    /// </summary>
    public static double FactorForDelta(int delta)
    {
        var detents = Math.Clamp(delta / 120.0, -4.0, 4.0);
        return Math.Pow(FactorPerDetent, detents);
    }
}
