namespace CaixaMaker.App.Models;

/// <summary>
/// Paleta explícita em ARGB para construção imperativa de modais (<c>ContentDialog</c>),
/// assegurando contraste AA/AAA consistente em Tema Claro e Tema Escuro independentemente do dicionário padrão.
/// </summary>
public readonly record struct DialogThemePalette(
    bool IsDark,
    uint DialogSurfaceArgb,
    uint CardSurfaceArgb,
    uint HeaderStripArgb,
    uint RowEvenArgb,
    uint RowAltArgb,
    uint BorderArgb,
    uint SubtleBorderArgb,
    uint TextPrimaryArgb,
    uint TextSecondaryArgb,
    uint AccentPrimaryArgb,
    uint AccentPrimaryHoverArgb,
    uint AccentHighlightArgb,
    uint AccentBadgeBgArgb,
    uint AccentBadgeBorderArgb,
    uint KbdBgArgb,
    uint KbdBorderArgb,
    uint KbdTextArgb,
    uint InputBgArgb,
    uint BrandPlaqueBgArgb,
    uint ButtonSurfaceArgb,
    uint ButtonHoverSurfaceArgb)
{
    public static DialogThemePalette Dark { get; } = new(
        IsDark: true,
        DialogSurfaceArgb: 0xFF14171C,
        CardSurfaceArgb: 0xFF181C22,
        HeaderStripArgb: 0xFF1E232B,
        RowEvenArgb: 0xFF181C22,
        RowAltArgb: 0xFF14181E,
        BorderArgb: 0xFF2A303A,
        SubtleBorderArgb: 0xFF343C49,
        TextPrimaryArgb: 0xFFF3F4F6,
        TextSecondaryArgb: 0xFF9CA3AF,
        AccentPrimaryArgb: 0xFF16A34A,
        AccentPrimaryHoverArgb: 0xFF15803D,
        AccentHighlightArgb: 0xFF22C55E,
        AccentBadgeBgArgb: 0x1F16A34A,
        AccentBadgeBorderArgb: 0x6616A34A,
        KbdBgArgb: 0xFF101317,
        KbdBorderArgb: 0xFF343C49,
        KbdTextArgb: 0xFF22C55E,
        InputBgArgb: 0xFF101317,
        BrandPlaqueBgArgb: 0xFFF4F6F8,
        ButtonSurfaceArgb: 0xFF222831,
        ButtonHoverSurfaceArgb: 0xFF28303B);

    public static DialogThemePalette Light { get; } = new(
        IsDark: false,
        DialogSurfaceArgb: 0xFFF4F6F9,
        CardSurfaceArgb: 0xFFFFFFFF,
        HeaderStripArgb: 0xFFEAEFF5,
        RowEvenArgb: 0xFFFFFFFF,
        RowAltArgb: 0xFFEAEFF5,
        BorderArgb: 0xFFC8D0DC,
        SubtleBorderArgb: 0xFFB7C1D0,
        TextPrimaryArgb: 0xFF111827,
        TextSecondaryArgb: 0xFF4B5563,
        AccentPrimaryArgb: 0xFF16A34A,
        AccentPrimaryHoverArgb: 0xFF15803D,
        AccentHighlightArgb: 0xFF16A34A,
        AccentBadgeBgArgb: 0x1A16A34A,
        AccentBadgeBorderArgb: 0x6616A34A,
        KbdBgArgb: 0xFFE2E8F0,
        KbdBorderArgb: 0xFFC8D0DC,
        KbdTextArgb: 0xFF0F172A,
        InputBgArgb: 0xFFFFFFFF,
        BrandPlaqueBgArgb: 0xFFFFFFFF,
        ButtonSurfaceArgb: 0xFFFFFFFF,
        ButtonHoverSurfaceArgb: 0xFFEAEFF5);

    public static DialogThemePalette ForTheme(ResolvedAppTheme theme)
        => theme == ResolvedAppTheme.Light ? Light : Dark;

    /// <summary>Calcula luminância relativa WCAG 2.1 no intervalo [0..1] para um RGB sRGB opaco.</summary>
    public static double RelativeLuminance(uint argb)
    {
        static double Linearize(byte channel)
        {
            double s = channel / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        byte r = (byte)((argb >> 16) & 0xFF);
        byte g = (byte)((argb >> 8) & 0xFF);
        byte b = (byte)(argb & 0xFF);
        return 0.2126 * Linearize(r) + 0.7152 * Linearize(g) + 0.0722 * Linearize(b);
    }

    /// <summary>Calcula razão de contraste WCAG entre duas cores opacas.</summary>
    public static double ContrastRatio(uint foregroundArgb, uint backgroundArgb)
    {
        double l1 = RelativeLuminance(foregroundArgb);
        double l2 = RelativeLuminance(backgroundArgb);
        double lighter = Math.Max(l1, l2);
        double darker = Math.Min(l1, l2);
        return (lighter + 0.05) / (darker + 0.05);
    }
}
