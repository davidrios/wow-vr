using System.Numerics;
using WowVR.Formats.World;
using Xunit;

namespace WowVR.Formats.Tests;

public class SkyCloudsTests
{
    private static SkyClouds.Look Look(float glow) => new(glow, Vector3.One, new Vector3(0.5f), new Vector3(0.1f), new Vector2(0.5f));

    private static byte[] Render(float glow, float time = 0f)
    {
        var pixels = new byte[SkyClouds.Size * SkyClouds.Size * 4];
        new SkyClouds().Render(pixels, time, Look(glow));
        return pixels;
    }

    private static double AverageAlpha(byte[] pixels) =>
        Enumerable.Range(0, pixels.Length / 4).Average(i => pixels[i * 4 + 3]);

    [Fact]
    public void NoGlowMeansAnEmptySky() => Assert.Equal(0d, AverageAlpha(Render(0f)));

    [Fact]
    public void MoreGlowMeansMoreCloud() => Assert.True(AverageAlpha(Render(0.75f)) > AverageAlpha(Render(0.5f)));

    [Fact]
    public void TheSameMomentLooksTheSame() => Assert.Equal(Render(0.5f, 300f), Render(0.5f, 300f));

    [Fact]
    public void CloudsChangeOverTime() => Assert.NotEqual(Render(0.5f, 0f), Render(0.5f, 300f));

    [Fact]
    public void ALightOverheadIsInTheMiddle() =>
        Assert.Equal(new Vector2(0.5f), SkyClouds.LightPosition(Vector3.UnitY));

    [Theory]
    [InlineData(1f, 0f, 1f, 0.5f)]
    [InlineData(1f, 1f, 1f, 0.5f)]
    [InlineData(0f, 1f, 0.5f, 1f)]
    public void ALightLowerThan45DegreesStaysOnTheEdge(float x, float y, float u, float v)
    {
        var position = SkyClouds.LightPosition(Vector3.Normalize(new Vector3(x, y, y == 1f && x == 0f ? 1f : 0f)));
        Assert.Equal(u, position.X, 3);
        Assert.Equal(v, position.Y, 3);
    }
}
