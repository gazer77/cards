using SkiaSharp;

namespace Cards.Rendering;

public sealed class DefaultTableTheme : ITableTheme
{
    public string Id          => "casino-green";
    public string DisplayName => "Casino Green";

    public SKColor FeltColor              => new(0x18, 0x4A, 0x2C);
    public SKColor FeltEdgeColor          => new(0x10, 0x32, 0x1E);
    public SKColor ZoneOutlineColor       => new(0xFF, 0xFF, 0xFF, 0x30);
    public SKColor ZoneLabelColor         => new(0xFF, 0xFF, 0xFF, 0x40);
    public SKColor PlayerNameColor        => new(0xF0, 0xEB, 0xE0, 0xCC);
    public SKColor CurrentPlayerHighlight => new(0xC8, 0xA9, 0x6E);
}
