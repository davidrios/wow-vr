using WowVR.Formats.World;
using Xunit;

namespace WowVR.Formats.Tests;

public class StarBrightnessTests
{
    [Theory]
    [InlineData(0, 1f)]
    [InlineData(3, 1f)]
    [InlineData(3.75, 0.5f)]
    [InlineData(4.5, 0f)]
    [InlineData(12, 0f)]
    [InlineData(22.5, 0f)]
    [InlineData(23.25, 0.5f)]
    public void StarsFollowTheClock(double hour, float brightness) =>
        Assert.Equal(brightness, SunPath.StarBrightness((float)(hour * 120)), 3);
}
