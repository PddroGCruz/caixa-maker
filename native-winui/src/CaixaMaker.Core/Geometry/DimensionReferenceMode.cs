namespace CaixaMaker.Core.Geometry;

/// <summary>
/// Modo de referência adotado pelo usuário para editar e manter as dimensões principais da caixa.
/// </summary>
public enum DimensionReferenceMode
{
    /// <summary>As entradas representam largura, profundidade e altura externas nominais (<c>W_ext, D_ext, H_ext</c>).</summary>
    External,

    /// <summary>As entradas representam o vão interno útil (<c>W_int, D_int, H_int</c>), preservado quando a espessura muda.</summary>
    InternalUsable,
}

/// <summary>
/// Conversões matemáticas exatas entre dimensões externas nominais e dimensões internas úteis
/// com paridade com <see cref="BoxModels.ComputedDimensionsSummary"/>.
/// </summary>
public static class DimensionReferenceConverter
{
    /// <summary>Converte medidas externas nominais (<c>W_ext, D_ext, H_ext</c>) em vão interno útil.</summary>
    public static (double InnerWidth, double InnerDepth, double InnerHeight) ToInternalUsable(
        double externalWidth,
        double externalDepth,
        double externalHeight,
        double thicknessMm)
    {
        return (
            Math.Round(externalWidth - 2.0 * thicknessMm, 4),
            Math.Round(externalDepth - 2.0 * thicknessMm, 4),
            Math.Round(externalHeight - thicknessMm, 4));
    }

    /// <summary>Converte vão interno útil desejado (<c>W_int, D_int, H_int</c>) em medidas externas nominais.</summary>
    public static (double ExternalWidth, double ExternalDepth, double ExternalHeight) ToExternal(
        double innerWidth,
        double innerDepth,
        double innerHeight,
        double thicknessMm)
    {
        return (
            Math.Round(innerWidth + 2.0 * thicknessMm, 4),
            Math.Round(innerDepth + 2.0 * thicknessMm, 4),
            Math.Round(innerHeight + thicknessMm, 4));
    }
}
