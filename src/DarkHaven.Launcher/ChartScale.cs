namespace DarkHaven.Launcher;

/// <summary>
/// The y-axis of the online graph: a round top and round ticks (0 / 10 / 20 / 30, never 0 / 7 / 14),
/// so every gridline names a value a player can read at a glance.
/// </summary>
public static class ChartScale
{
    private static readonly int[] Steps = [1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000];

    /// <summary>Tick step and axis top for data reaching <paramref name="max"/>, aiming at ~4 intervals.</summary>
    public static (int Step, int Top) For(double max)
    {
        max = Math.Max(1, max);
        var step = Steps.FirstOrDefault(s => max / s <= 5, Steps[^1]);
        var top = (int)Math.Ceiling(max / step) * step;
        return (step, top);
    }

    public static IEnumerable<int> Ticks(int step, int top)
    {
        for (var v = 0; v <= top; v += step)
            yield return v;
    }
}
