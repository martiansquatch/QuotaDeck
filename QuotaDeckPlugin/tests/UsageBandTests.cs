namespace QuotaDeck.Core.Tests;

using QuotaDeck.Core.Model;

/// <summary>Mirrors Codenotch <c>Tests/UsageBandTests.swift</c> (<c>testBandsMatchTheDesignFrame</c>,
/// <c>testBoundaries</c>).</summary>
public class UsageBandTests
{
    /// <summary>The three levels the mockup renders: 21% green, 52% yellow, 73% orange.</summary>
    [Fact]
    public void BandsMatchTheDesignFrame()
    {
        Assert.Equal(UsageBand.Ample, Bands.For(0.21));
        Assert.Equal(UsageBand.Watch, Bands.For(0.52));
        Assert.Equal(UsageBand.Critical, Bands.For(0.73));
    }

    [Theory]
    [InlineData(0.0, UsageBand.Ample)]
    [InlineData(0.4999, UsageBand.Ample)]
    [InlineData(0.50, UsageBand.Watch)]
    [InlineData(0.6999, UsageBand.Watch)]
    [InlineData(0.70, UsageBand.Critical)]
    [InlineData(0.9999, UsageBand.Critical)]
    [InlineData(1.0, UsageBand.Exhausted)]
    [InlineData(1.4, UsageBand.Exhausted)]
    public void Boundaries(double fraction, UsageBand expected)
    {
        Assert.Equal(expected, Bands.For(fraction));
    }

    /// <summary>The watch limit moves the ample/watch edge with it.</summary>
    [Fact]
    public void AWatchLimitMovesTheEdge()
    {
        Assert.Equal(UsageBand.Ample, Bands.For(0.29, watchLimit: 0.30));
        Assert.Equal(UsageBand.Watch, Bands.For(0.30, watchLimit: 0.30));
        Assert.Equal(UsageBand.Watch, Bands.For(0.39, watchLimit: 0.30, criticalLimit: 0.40));
        Assert.Equal(UsageBand.Critical, Bands.For(0.40, watchLimit: 0.30, criticalLimit: 0.40));
    }
}
