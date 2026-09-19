using Xunit;

namespace DarkHaven.Launcher.Tests;

public class ChartScaleTests
{
    [Theory]
    [InlineData(30, 10, 30)]  // a 30-slot server: 0 / 10 / 20 / 30
    [InlineData(17, 5, 20)]
    [InlineData(4, 1, 4)]
    [InlineData(0, 1, 1)]     // an empty server still gets an axis
    [InlineData(64, 20, 80)]
    [InlineData(120, 25, 125)]
    public void Picks_a_round_step_and_top(double max, int step, int top) =>
        Assert.Equal((step, top), ChartScale.For(max));

    [Fact]
    public void Ticks_run_from_zero_to_the_top() =>
        Assert.Equal([0, 5, 10, 15, 20], ChartScale.Ticks(5, 20));
}
