using SkiaSharp;

namespace Cards.Rendering;

public sealed class DefaultCardSkin : ICardSkin
{
    public string Id          => "classic";
    public string DisplayName => "Classic";

    public SKColor BackColor        => new(0x1A, 0x3A, 0x8A);
    public SKColor BackPatternColor => new(0x22, 0x4A, 0xA8, 0x60);
    public SKColor BackBorderColor  => new(0x12, 0x28, 0x66);

    public SKColor FaceColor        => new(0xFF, 0xFD, 0xF5);
    public SKColor FaceBorderColor  => new(0xC8, 0xC6, 0xBC);
    public SKColor RedSuitColor     => new(0xCC, 0x22, 0x22);
    public SKColor BlackSuitColor   => new(0x1A, 0x1A, 0x1A);
    public SKColor WildFaceColor    => new(0xFF, 0xF6, 0xD5);   // warm cream, reads as "special" at a glance
    public SKColor WildBorderColor  => new(0xD4, 0xA0, 0x17);   // gold, matching the selection glow

    public float CornerRadiusFraction => 0.08f;
    public CardFaceStyle FaceStyle => CardFaceStyle.Classic;
}
