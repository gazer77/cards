using Cards.Rendering;

namespace Cards.Tests;

/// <summary>
/// The pixel-ratio cap decides both how much work the renderer does and where taps
/// land. A mistake here does not look like a bug — the table renders correctly and
/// every tap simply misses.
/// </summary>
public sealed class DisplayScalingTests
{
    [Theory]
    [InlineData(1.0)]   // ordinary desktop
    [InlineData(1.25)]  // scaled Windows desktop
    [InlineData(1.5)]   // exactly at the cap
    public void Leaves_displays_at_or_below_the_cap_untouched(double dpr)
    {
        var s = DisplayScaling.For(dpr, 1.5);

        Assert.Equal(dpr, s.PixelRatio, 3);
        Assert.Equal(1.0, s.RenderScale, 3);
    }

    [Theory]
    [InlineData(2.0)]   // typical tablet / Retina laptop
    [InlineData(3.0)]   // typical phone
    [InlineData(4.0)]
    public void Caps_dense_displays(double dpr)
    {
        var s = DisplayScaling.For(dpr, 1.5);

        Assert.Equal(1.5, s.PixelRatio, 3);
        Assert.True(s.RenderScale < 1.0);
    }

    /// <summary>
    /// The invariant the whole scheme rests on: shrinking the element by RenderScale
    /// and letting the browser multiply by devicePixelRatio must land exactly on the
    /// capped ratio. If these disagree, the canvas is a different size than the code
    /// converting pointer coordinates believes.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(3.0)]
    public void Element_shrink_and_device_ratio_multiply_back_to_the_capped_ratio(double dpr)
    {
        var s = DisplayScaling.For(dpr, 1.5);

        const double cssWidth = 800;
        double actualBacking = cssWidth * s.RenderScale * dpr;

        Assert.Equal(s.BackingPixels(cssWidth), actualBacking, 3);
    }

    [Fact]
    public void A_phone_renders_far_fewer_pixels_than_it_asks_for()
    {
        var s = DisplayScaling.For(3.0, 1.5);

        // 390x844 CSS at DPR 3 is 2.96M pixels — more than a 1920x766 desktop table,
        // on a fraction of the CPU. The cap is what makes that survivable.
        double uncapped = 390 * 3.0 * (844 * 3.0);
        double capped   = s.BackingPixels(390) * s.BackingPixels(844);

        Assert.True(capped * 3.5 < uncapped,
            $"Expected a large saving, got {uncapped / capped:F1}x.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void Degrades_to_one_to_one_rather_than_a_zero_sized_canvas(double dpr)
    {
        var s = DisplayScaling.For(dpr, 1.5);

        Assert.True(s.PixelRatio > 0);
        Assert.True(s.RenderScale > 0);
    }
}
