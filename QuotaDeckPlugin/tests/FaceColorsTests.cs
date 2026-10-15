namespace QuotaDeck.Core.Tests;

using QuotaDeck.Core.Faces;
using QuotaDeck.Core.Model;
using QuotaDeck.Core.Readers;

/// <summary>
/// Band → colour: Codenotch's watch/critical palette, the provider's own accent for an ample or
/// absent band, and grey for anything dimmed — ring and text alike.
/// </summary>
public class FaceColorsTests
{
    private static readonly ProviderPalette Accent = new(0x12, 0x34, 0x56);

    [Fact]
    public void AnAmpleBandDrawsTheProviderAccent()
    {
        Assert.Equal(Accent, FaceColors.Accent(UsageBand.Ample, Accent));
    }

    /// <summary>A face with no denominator has no band; per the FaceFigures rule it keeps the
    /// provider's accent rather than picking a default colour.</summary>
    [Fact]
    public void AnAbsentBandKeepsTheProviderAccent()
    {
        Assert.Equal(Accent, FaceColors.Accent(null, Accent));
    }

    [Fact]
    public void WatchDrawsTheWatchPalette()
    {
        Assert.Equal(new ProviderPalette(0xF2, 0xFF, 0x00), FaceColors.Accent(UsageBand.Watch, Accent));
    }

    [Theory]
    [InlineData(UsageBand.Critical)]
    [InlineData(UsageBand.Exhausted)]
    public void CriticalAndExhaustedShareTheCriticalPalette(UsageBand band)
    {
        Assert.Equal(new ProviderPalette(0xFF, 0x3F, 0x00), FaceColors.Accent(band, Accent));
    }

    [Fact]
    public void ADimmedReadingIsGreyWhateverTheBand()
    {
        Assert.Equal(new ProviderPalette(0x80, 0x80, 0x80), FaceColors.Accent(UsageBand.Critical, Accent, dimmed: true));
        Assert.Equal(new ProviderPalette(0x80, 0x80, 0x80), FaceColors.Accent(UsageBand.Ample, Accent, dimmed: true));
        Assert.Equal(new ProviderPalette(0x80, 0x80, 0x80), FaceColors.Accent(null, Accent, dimmed: true));
        Assert.Equal(0x60, FaceColors.DimmedAlpha);
    }
}
