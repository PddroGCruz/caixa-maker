namespace CaixaMaker.App.Models;

/// <summary>Preferência explícita de tema visual da aplicação.</summary>
public enum AppThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>Tema efetivamente resolvido para renderização da interface.</summary>
public enum ResolvedAppTheme
{
    Light,
    Dark,
}

/// <summary>As três áreas funcionais do espaço de trabalho CAD/CAM.</summary>
public enum WorkspaceArea
{
    Modeling,
    Personalization,
    Fabrication,
}

/// <summary>Modos de visualização do viewport central.</summary>
public enum CenterViewMode
{
    Mode3D,
    Mode2D,
    Split,
}

/// <summary>Orientação da divisão entre Viewport 3D e Editor/Plano 2D no modo dividido.</summary>
public enum SplitViewportOrientation
{
    VerticalStacked,
    HorizontalSideBySide,
}

/// <summary>Predefinições de câmera rápida no visualizador 3D.</summary>
public enum CameraPreset
{
    Isometric,
    Front,
    Back,
    Left,
    Right,
    Top,
}

/// <summary>Os 5 alinhamentos rápidos de elementos relativos à área segura da face.</summary>
public enum QuickAlignmentPreset
{
    CenterSurface,
    AlignLeft,
    AlignRight,
    AlignTop,
    AlignBottom,
}

/// <summary>Decisão do usuário diante de fechamento ou navegação com alterações não salvas.</summary>
public enum UnsavedChangesDecision
{
    SaveAndContinue,
    DiscardAndContinue,
    Cancel,
}
