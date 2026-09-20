using SkiaSharp;
using Cards.Engine;

namespace Cards.Rendering;

/// <summary>
/// Where every zone sits on the table.
///
/// Delegates to <see cref="DeclaredLayoutEngine"/>, which reads the placement from the
/// definition, and then applies each zone's declared <c>card_scale</c>. Two hand-tuned
/// layouts — one for two players, one for more — lived here until every shipped game
/// declared its own; they placed the zones they knew by name and let the rest fall off
/// the table, which is how a newly declared zone could be invisible until the renderer
/// learned it.
/// </summary>
public static class ZoneLayoutEngine
{
    public static IReadOnlyList<ZoneLayout> Compute(GameState state, SKImageInfo canvasInfo)
    {
        var layouts = DeclaredLayoutEngine.Compute(state, canvasInfo);

        // A zone's declared card_scale, applied last so the layout engine need not know.
        // Bounds stay put; the cards inside grow or shrink, and the drawers already fit
        // cards to their bounds.
        return layouts.Select(l =>
        {
            float scale = l.Zone.Definition?.CardScale ?? 1f;
            return scale == 1f
                ? l
                : l with { CardWidth = l.CardWidth * scale, CardHeight = l.CardHeight * scale };
        }).ToList();
    }
}
