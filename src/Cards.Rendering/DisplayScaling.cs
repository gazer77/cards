namespace Cards.Rendering;

/// <summary>
/// Works out how many canvas pixels to render per CSS pixel, and how much to shrink
/// the canvas element to get there.
///
/// Skia rasterises on the CPU in the browser, so cost scales with pixel count — and a
/// dense phone screen asks for more pixels than a desktop while having far less power
/// to draw them. Capping the ratio trades a little sharpness for a quadratic saving.
///
/// Extracted from the canvas component because getting it wrong does not look wrong:
/// the table still renders, but every tap lands somewhere other than where it was
/// aimed, by a factor nobody notices on a 1x monitor.
/// </summary>
public readonly record struct DisplayScaling
{
    /// <summary>Canvas pixels per CSS pixel, after capping.</summary>
    public double PixelRatio { get; private init; }

    /// <summary>
    /// Fraction of its natural size the canvas element should be laid out at. The
    /// element's backing store is (its CSS size x devicePixelRatio), so shrinking it
    /// is the only way to request fewer pixels than the display's density implies;
    /// CSS then scales it back up so the table still fills the same space. 1.0 means
    /// no capping was needed.
    /// </summary>
    public double RenderScale { get; private init; }

    public static DisplayScaling For(double devicePixelRatio, double maxPixelRatio)
    {
        // A missing or nonsensical devicePixelRatio must degrade to 1:1 rather than
        // producing a zero-sized canvas.
        if (devicePixelRatio is <= 0 or double.NaN) devicePixelRatio = 1.0;
        if (maxPixelRatio    is <= 0 or double.NaN) maxPixelRatio    = 1.0;

        double ratio = Math.Min(devicePixelRatio, maxPixelRatio);

        return new DisplayScaling
        {
            PixelRatio  = ratio,
            RenderScale = ratio / devicePixelRatio,
        };
    }

    /// <summary>
    /// Canvas pixels a region of <paramref name="cssPixels"/> will occupy. Present so
    /// the relationship the whole scheme depends on can be asserted directly.
    /// </summary>
    public double BackingPixels(double cssPixels) => cssPixels * PixelRatio;
}
