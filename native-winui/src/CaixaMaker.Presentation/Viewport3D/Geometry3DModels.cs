using System.Numerics;
using CaixaMaker.Core.Personalization;

namespace CaixaMaker.App.Viewport3D;

/// <summary>Classificação dos triângulos da chapa MDF para sombreamento realista (face x borda queimada a laser).</summary>
public enum PanelMaterialKind
{
    OuterFace,
    InnerFace,
    CutEdge,
}

/// <summary>Triângulo tridimensional já posicionado em coordenadas de mundo (mm).</summary>
public readonly record struct MeshTriangle3D(
    Vector3 A,
    Vector3 B,
    Vector3 C,
    Vector3 Normal,
    PanelMaterialKind Kind);

/// <summary>Aresta de vinco/borda para realce CAD dos dentes Finger Joint e furos.</summary>
public readonly record struct EdgeSegment3D(
    Vector3 Start,
    Vector3 End,
    bool IsBoundaryCrease);

/// <summary>Polígono delimitador 3D projetado de um elemento de personalização sobre uma chapa.</summary>
public sealed record ProjectedElement3D(
    string ElementId,
    IReadOnlyList<Vector3> Corners3D);

/// <summary>Plano 3D da superfície externa correspondente ao mapeamento <see cref="SurfaceMapper.SurfaceTo3DLocal"/>.</summary>
public sealed record SurfaceOverlayQuad3D(
    SurfaceId SurfaceId,
    string PartId,
    double WidthMm,
    double HeightMm,
    Vector3 TopLeft,
    Vector3 TopRight,
    Vector3 BottomRight,
    Vector3 BottomLeft,
    Vector3 Normal,
    IReadOnlyList<ProjectedElement3D> ProjectedElements);

/// <summary>Malha extrudada individual de uma chapa da caixa MDF com furos reais.</summary>
public sealed record ExtrudedPanel3D(
    SurfaceId SurfaceId,
    string PartId,
    string Label,
    Vector3 Center3D,
    Vector3 ExplodeNormal,
    bool FollowsLidHinge,
    IReadOnlyList<MeshTriangle3D> Triangles,
    IReadOnlyList<EdgeSegment3D> CreaseEdges,
    SurfaceOverlayQuad3D? OverlayQuad);

/// <summary>Eixo dimensional paramétrico editável diretamente nas cotas do Viewport 3D.</summary>
public enum BoxDimensionAxis
{
    WidthX,
    DepthZ,
    HeightY,
}

/// <summary>Cota dimensional 3D com linhas de chamada e badge técnico.</summary>
public sealed record DimensionCallout3D(
    Vector3 Start,
    Vector3 End,
    Vector3 ExtStart,
    Vector3 ExtEnd,
    Vector3 LabelPos,
    string Label,
    string? Sublabel,
    BoxDimensionAxis Axis = BoxDimensionAxis.WidthX);

/// <summary>Cena 3D paramétrica completa da caixa montada, articulada ou explodida.</summary>
public sealed record BoxScene3D(
    string ModelId,
    double WidthMm,
    double DepthMm,
    double HeightMm,
    double ThicknessMm,
    double LidAngleDeg,
    bool IsExplodedView,
    Vector3 LidHingeOrigin,
    IReadOnlyList<ExtrudedPanel3D> Panels,
    IReadOnlyList<DimensionCallout3D> Dimensions,
    Vector3 BoundingMin,
    Vector3 BoundingMax)
{
    public ExtrudedPanel3D? FindPanel(SurfaceId surfaceId)
        => Panels.FirstOrDefault(p => p.SurfaceId == surfaceId);
}
