using SkiaSharp;
using Cards.Engine;

namespace Cards.Rendering;

/// <summary>
/// Where every zone sits on the table.
///
/// Delegates to <see cref="DeclaredLayoutEngine"/>, which reads the placement from the
/// definition, and then applies each zone's declared <c>card_scale</c> and whatever the
/// player has chosen on top of it. Two hand-tuned layouts — one for two players, one for
/// more — lived here until every shipped game declared its own; they placed the zones
/// they knew by name and let the rest fall off the table, which is how a newly declared
/// zone could be invisible until the renderer learned it.
/// </summary>
public static class ZoneLayoutEngine
{
    /// <param name="cardScale">
    /// The player's own size for cards, multiplied into every zone's declared scale.
    /// Bounds stay put: a zone keeps its share of the table and the cards inside it
    /// grow or shrink, which is what lets a long hand stop overlapping itself.
    /// </param>
    public static IReadOnlyList<ZoneLayout> Compute(
        GameState state, SKImageInfo canvasInfo, float cardScale = 1f)
    {
        var layouts = DeclaredLayoutEngine.Compute(state, canvasInfo);

        // Applied last so the layout engine need not know, and the drawers already fit
        // cards to their bounds.
        return layouts.Select(l =>
        {
            float scale = (l.Zone.Definition?.CardScale ?? 1f) * cardScale;
            return scale == 1f
                ? l
                : l with { CardWidth = l.CardWidth * scale, CardHeight = l.CardHeight * scale };
        }).ToList();
    }
}
