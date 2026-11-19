using CaixaMaker.App.Models;

namespace CaixaMaker.App.Services;

/// <summary>Classificação dos tipos de controle que podem deter foco de teclado.</summary>
public enum FocusControlKind
{
    NonEditable,
    TextBox,
    NumberBox,
    RichEditBox,
    EditableComboBox,
    AutoSuggestBox,
    PasswordBox,
}

/// <summary>Contexto de foco avaliado para decidir se um atalho global/workspace deve atuar.</summary>
public readonly record struct KeyboardFocusContext(
    FocusControlKind ControlKind,
    bool IsReadOnly = false,
    bool HasOpenModal = false);

/// <summary>Identificadores canônicos de comandos de menu e aceleradores.</summary>
public static class DesktopCommandIds
{
    public const string FileNew = "file.new";
    public const string FileOpenProject = "file.open_project";
    public const string FileOpenRecentPrefix = "file.open_recent:";
    public const string FileSave = "file.save";
    public const string FileSaveAs = "file.save_as";
    public const string FileImportPackage = "file.import_package";
    public const string FileExportPackage = "file.export_package";
    public const string FileProjectProperties = "file.project_properties";
    public const string FileBackToCatalog = "file.back_to_catalog";
    public const string FileExit = "file.exit";

    public const string EditUndo = "edit.undo";
    public const string EditRedo = "edit.redo";
    public const string EditCopy = "edit.copy";
    public const string EditPaste = "edit.paste";
    public const string EditDuplicate = "edit.duplicate";
    public const string EditDelete = "edit.delete";
    public const string EditSelectAll = "edit.select_all";
    public const string EditClearAll = "edit.clear_all";
    public const string EditResetParameters = "edit.reset_parameters";

    public const string ViewMode3D = "view.mode_3d";
    public const string ViewMode2D = "view.mode_2d";
    public const string ViewModeSplit = "view.mode_split";
    public const string ViewToggleLeftSidebar = "view.toggle_left_sidebar";
    public const string ViewToggleRightSidebar = "view.toggle_right_sidebar";
    public const string ViewToggleToolbar = "view.toggle_toolbar";
    public const string ViewToggleDimensions = "view.toggle_dimensions";
    public const string ViewResetCamera = "view.reset_camera";
    public const string ViewThemeSystem = "view.theme_system";
    public const string ViewThemeLight = "view.theme_light";
    public const string ViewThemeDark = "view.theme_dark";

    public const string ProjectAreaModeling = "project.area_modeling";
    public const string ProjectAreaPersonalization = "project.area_personalization";
    public const string ProjectAreaFabrication = "project.area_fabrication";
    public const string ProjectToggleExploded = "project.toggle_exploded";
    public const string ProjectToggleLid = "project.toggle_lid";
    public const string ProjectToggleModel = "project.toggle_model";
    public const string ProjectFocusText = "project.add_text";
    public const string ProjectImportSvg = "project.import_svg";
    public const string ProjectImportRaster = "project.import_raster";
    public const string ProjectValidate = "project.validate";

    public const string ToolSelect = "tool.select";
    public const string ToolDrawRectangle = "tool.draw_rectangle";
    public const string ToolDrawCircle = "tool.draw_circle";
    public const string ToolDrawSlot = "tool.draw_slot";
    public const string ToolDrawPolygon = "tool.draw_polygon";
    public const string ToolDrawCrosshair = "tool.draw_crosshair";
    public const string ToolCancel = "tool.cancel";

    public const string ToolsExportSvg = "tools.export_svg";
    public const string ToolsExportDxf = "tools.export_dxf";
    public const string ToolsExportPdf = "tools.export_pdf";
    public const string ToolsExportZip = "tools.export_zip";
    public const string ToolsSavedProjects = "tools.saved_projects";
    public const string ToolsPreferences = "tools.preferences";

    public const string WindowToggleFullscreen = "window.toggle_fullscreen";
    public const string WindowMinimize = "window.minimize";
    public const string WindowToggleMaximize = "window.toggle_maximize";

    public const string HelpAbout = "help.about";
    public const string HelpShortcuts = "help.shortcuts";
    public const string HelpInstitution = "help.institution";
}

/// <summary>
/// Guarda centralizada de atalhos de teclado impedindo captura de <c>Delete</c>, <c>Ctrl+C/V/Z/Y/D</c>
/// quando o foco está dentro de campos editáveis (<c>TextBox</c>, <c>NumberBox</c>, <c>RichEditBox</c>, etc.).
/// </summary>
public static class KeyboardShortcutGuard
{
    public static bool IsEditableKeyboardTarget(KeyboardFocusContext context)
    {
        if (context.IsReadOnly)
        {
            return false;
        }

        return context.ControlKind is FocusControlKind.TextBox
            or FocusControlKind.NumberBox
            or FocusControlKind.RichEditBox
            or FocusControlKind.EditableComboBox
            or FocusControlKind.AutoSuggestBox
            or FocusControlKind.PasswordBox;
    }

    public static bool ShouldDispatchCommand(KeyboardFocusContext context, string commandId)
    {
        if (context.HasOpenModal)
        {
            return false;
        }

        if (IsEditableKeyboardTarget(context))
        {
            return commandId switch
            {
                DesktopCommandIds.EditDelete => false,
                DesktopCommandIds.EditSelectAll => false,
                DesktopCommandIds.EditCopy => false,
                DesktopCommandIds.EditPaste => false,
                DesktopCommandIds.EditDuplicate => false,
                DesktopCommandIds.EditUndo => false,
                DesktopCommandIds.EditRedo => false,
                DesktopCommandIds.ToolSelect => false,
                DesktopCommandIds.ToolDrawRectangle => false,
                DesktopCommandIds.ToolDrawCircle => false,
                DesktopCommandIds.ToolDrawSlot => false,
                DesktopCommandIds.ToolDrawPolygon => false,
                DesktopCommandIds.ToolDrawCrosshair => false,
                DesktopCommandIds.ToolCancel => false,
                _ => true,
            };
        }

        return true;
    }

    public static IReadOnlyList<ShortcutReferenceItem> GetReferenceShortcuts() =>
    [
        // Projeto e Arquivo
        new("Projeto e Arquivo", "Ctrl + N", "Novo projeto no modelo atual", "Designer"),
        new("Projeto e Arquivo", "Ctrl + O", "Abrir gerenciador de projetos salvos", "Global"),
        new("Projeto e Arquivo", "Ctrl + Shift + O", "Importar projeto portátil (.cmaker / .caixamaker.json)", "Global"),
        new("Projeto e Arquivo", "Ctrl + S", "Salvar projeto (.cmaker)", "Designer"),
        new("Projeto e Arquivo", "Ctrl + Shift + S", "Salvar projeto como...", "Designer"),
        new("Projeto e Arquivo", "Ctrl + E", "Exportar projeto autocontido (.cmaker)", "Designer"),
        new("Projeto e Arquivo", "Alt + F4", "Encerrar aplicação", "Global"),

        // Edição e Elementos
        new("Edição e Elementos", "Ctrl + Z", "Desfazer alteração de personalização (até 40 passos)", "Personalização"),
        new("Edição e Elementos", "Ctrl + Y", "Refazer alteração de personalização", "Personalização"),
        new("Edição e Elementos", "Ctrl + C", "Copiar elemento selecionado", "Personalização"),
        new("Edição e Elementos", "Ctrl + V", "Colar elemento na face ativa", "Personalização"),
        new("Edição e Elementos", "Ctrl + D", "Duplicar elemento selecionado", "Personalização"),
        new("Edição e Elementos", "Ctrl + A", "Selecionar todos os elementos da chapa ativa", "Personalização"),
        new("Edição e Elementos", "Delete", "Excluir elemento(s) selecionado(s) (bloqueado em campos de texto)", "Personalização"),
        new("Edição e Elementos", "Ctrl + Shift + Backspace", "Limpar todos os elementos", "Personalização"),
        new("Ferramentas CAD 2D", "V", "Ferramenta Selecionar / Mover", "Editor 2D"),
        new("Ferramentas CAD 2D", "R", "Desenhar Retângulo (click-and-drag)", "Editor 2D"),
        new("Ferramentas CAD 2D", "C", "Desenhar Círculo / Furo (click-and-drag)", "Editor 2D"),
        new("Ferramentas CAD 2D", "S", "Desenhar Oblongo / Slot (click-and-drag)", "Editor 2D"),
        new("Ferramentas CAD 2D", "P", "Desenhar Polígono Regular (click-and-drag)", "Editor 2D"),
        new("Ferramentas CAD 2D", "X", "Desenhar Cruz de Centro (click-and-drag)", "Editor 2D"),
        new("Ferramentas CAD 2D", "Esc", "Cancelar desenho ativo / retornar para Seleção", "Editor 2D"),

        // Visualização e Áreas
        new("Visualização e Áreas", "Alt + 1", "Ir para área Modelagem", "Designer"),
        new("Visualização e Áreas", "Alt + 2", "Ir para área Personalização", "Designer"),
        new("Visualização e Áreas", "Alt + 3", "Ir para área Fabricação", "Designer"),
        new("Visualização e Áreas", "Ctrl + 1 / 2 / 3", "Alternar viewport 3D / 2D / Dividido", "Designer"),
        new("Visualização e Áreas", "Ctrl + B", "Alternar painel esquerdo", "Designer"),
        new("Visualização e Áreas", "Ctrl + Shift + B", "Alternar painel direito", "Designer"),
        new("Visualização e Áreas", "Ctrl + R", "Restaurar câmera 3D padrão", "Designer"),
        new("Visualização e Áreas", "Alt + E", "Alternar vista explodida 3D", "Designer"),
        new("Visualização e Áreas", "Alt + D", "Abrir / fechar tampa articulada", "Modelo com Tampa"),
        new("Visualização e Áreas", "F11", "Alternar tela cheia", "Global"),

        // Exportação e Ajuda
        new("Exportação e Ajuda", "F5", "Executar validação geométrica SAT", "Designer"),
        new("Exportação e Ajuda", "Ctrl + Shift + V", "Alternar visibilidade das cotas 3D", "Designer"),
        new("Exportação e Ajuda", "Ctrl + ,", "Abrir Preferências e Configurações", "Global"),
        new("Exportação e Ajuda", "F1", "Abrir Sobre e Atalhos", "Global"),
    ];
}
