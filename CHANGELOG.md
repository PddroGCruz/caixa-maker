# Changelog — Caixa Maker

Todas as alterações relevantes do **Caixa Maker** (aplicação Web e Desktop Windows) seguem o padrão **Semantic Versioning (`MAJOR.MINOR.PATCH`)** definido em [docs/VERSIONING_POLICY.md](docs/VERSIONING_POLICY.md) a partir do arquivo central `VERSION`.

---

## [2.4.0] - 2026-11-18

### Adicionado (v2.4.0 — Otimizador de Corte 2D True-Shape Nesting, Importadores DXF/SVG e Lote Integrado)
- **Motor Nativo de Otimização de Corte 2D (*True-Shape Nesting*) (Stage 27):**
  - Implementado `TrueShapeNestingEngine` determinístico multi-estratégia (`AreaDescending`, `MaxDimensionDescending`, `BoundingAreaDescending`) com encaixe em concavidades reais, suporte a alocação de peças menores dentro de furos internos fechados (`AllowNestingInHoles`), políticas de rotação (`Locked0`, `Step180`, `Step90`, `Step45`), folga mínima configurável entre cortes (`PartSpacingMm`) e distribuição automática multi-chapa para aproveitamento de retalhos.
- **Importadores 1:1 (DXF & SVG), Adaptador em Memória da Caixa e Exportador 1:1 (Stage 27):**
  - Criado leitor nativo ASCII DXF (`DxfImporter`) com conversão automática de `$INSUNITS` para milímetros, suporte a `LINE`, `LWPOLYLINE` (com *bulge*), `POLYLINE`/`VERTEX`/`SEQEND`, `CIRCLE`, `ARC`, `ELLIPSE` e `SPLINE` (De Boor), costura automática de contornos abertos (`<= 0,05 mm`) e agrupamento hierárquico Ponto-em-Polígono de peças, furos internos e gravações.
  - Adicionados `SvgNestingImporter` 1:1, `BoxPanelNestingAdapter` (extração direta em memória chapa por chapa com personalizações, zero exportação intermediária) e `NestingSheetExporter` para exportação 1:1 em **SVG**, **DXF R2000 (`AC1015`)**, **PDF 1.4** e pacote `.ZIP` multi-chapa nas 6 layers semânticas CAM + layer auxiliar `SHEET_BOUNDS`.
- **Interface Dedicada "Otimizador de Corte 2D (Nesting)" (Stage 28):**
  - Nova tela dedicada `NestingWorkspaceView` em 3 colunas com fila de peças (`NestingWorkspaceViewModel`), multiplicador de lote da caixa ativa (`BoxCopiesCount`), mistura com arquivos SVG/DXF externos, presets rápidos de retalho/mesa laser ativa, renderizador SkiaSharp com cache vetorial `NestingSheetStaticCache` (pan/zoom a 60+ FPS), execução assíncrona cancelável e duplo ponto de acesso (Catálogo para uso independente e Etapa 3 de Fabricação para uso integrado preservando os caches do projeto).
- **Dimensionamento Adaptativo Unificado de Encaixes (*Finger Joints*) (Stage 29):**
  - Adicionados os modos `Auto` (padrão proporcional à espessura do MDF e ao comprimento útil de cada aresta), `Tamanho` (`TargetSizeMm`, `3,0` a `40,0 mm`) e `Qtd.` (`FixedCount`, `2` a `25` espigas com redução de segurança em arestas curtas) em `BoxParameters`, `BoxCalculator`, `BoxDrawer` e persistência retrocompatível `.cmaker`.
  - Controle segmentado compacto `Auto | Tamanho | Qtd.` integrado à seção `MATERIAL E AJUSTE CNC (KERF)` no `ModelingAreaLeftPanel` e resumo detalhado com largura efetiva dos dentes da base no `ModelingAreaRightPanel`.

---

## [2.3.0] - 2026-11-18

### Adicionado (v2.3.0 — Fluxo de Trabalho Guiado: Stepper 1 → 2 → 3 e Navegação Contextual)
- **Fluxo de Trabalho Guiado — Stepper 1 → 2 → 3 e Navegação Contextual (Stage 26):**
  - Conversão do seletor superior no `WorkspaceSplitShell` em trilha sequencial numerada (`1 Modelagem → 2 Personalização → 3 Fabricação`) com indicador `✓` de conclusão de etapa e dica orientativa dinâmica na barra de status (`WorkflowGuidanceHint`).
- **Rodapés Fixos de Ação nos Painéis Direitos:**
  - Botões contextuais de avanço e retorno fixados fora do `ScrollViewer` em `ModelingAreaRightPanel` (`Avançar para Personalização →` e `Pular direto para Fabricação »`), `PersonalizationAreaRightPanel` (`← Modelagem`, `Avançar para Fabricação →` e aviso SAT não bloqueante) e `ManufacturingAreaRightPanel` (`← Voltar para Personalização`), orquestrados por `NavigateNextWorkspaceCommand`, `NavigatePreviousWorkspaceCommand` e `SkipToManufacturingCommand` no `MainWindowViewModel`.

---

## [2.2.0] - 2026-11-18

### Adicionado (v2.2.0 — Seleção Múltipla Ordenada, Caixa Marquee e Alinhamento/Distribuição CAD)
- **Seleção Múltipla Ordenada, Caixa Marquee e Edição em Grupo (Stage 24):**
  - Lista ordenada `SelectedElementIds` em `PersonalizationWorkspaceViewModel` preservando estritamente a ordem cronológica de seleção (`FirstSelectedElementId` / `LastSelectedElementId`), inclusive na pilha Undo/Redo (`PersonalizationHistoryEntry` em `SurfaceManager`).
  - Suporte completo a `Shift + Clique` (`ToggleElementSelection`), seleção por caixa *marquee* no fundo vazio da chapa (`IsMarqueeSelecting` com adição via `Shift`, cancelamento via `Esc` e limpeza por clique vazio), arraste e exclusão atômica de grupo (`DeleteSelectedElements`, `TranslateSelectedElements`) e atalho `Ctrl + A` (`SelectAllElementsOnActiveSurface`) protegido pelo `KeyboardShortcutGuard`.
  - Renderização em overlay no `OffscreenViewportRenderer` com recursos `static readonly` (preservando 100% do `Editor2DStaticCache` em sub-6 ms/frame), destaque visual da âncora (`1º`) no canvas 2D e no Outliner e banner *in-place* no `PersonalizationAreaRightPanel`.
- **Motor de Alinhamento e Distribuição CAD — Estilo Inkscape (Stage 25):**
  - Criado `ElementAlignmentService` determinístico 1:1 em milímetros com 4 modos de referência (`SurfaceSafeArea`, `FirstSelected`, `LastSelected`, `SelectionBounds`), 6 ações de alinhamento (`AlignLeft`, `AlignCenterX`, `AlignRight`, `AlignTop`, `AlignCenterY`, `AlignBottom`) e 2 de distribuição equidistante de centros (`DistributeHorizontal`, `DistributeVertical`), considerando rotação real e `AnchorMode.Center`/`TopLeft`.
  - Integração transacional com 1 passo atômico de Undo/Redo + `ValidatePanel` e adição da seção contextual *in-place* **"Alinhar & Distribuir"** (`Relativo a:` + botões horizontais, verticais e distribuição) no `PersonalizationAreaRightPanel`.

---

## [2.1.0] - 2026-11-18

### Adicionado (v2.1.0 — Primitivas Geométricas Paramétricas 1:1, Desenho Click-and-Drag 2D e Ferramentas CAD)
- **Primitivas Geométricas Paramétricas 1:1 (Stage 22 — `CaixaMaker.Core`, `CaixaMaker.Cam` e `.cmaker`):**
  - Implementados `ElementType.Shape`, `GeometricShapeKind` (`Rectangle` com raio de canto, `Circle`, `Slot`, `Polygon` regular e `Crosshair`), `OperationMode` (`Cut`, `Engrave`, `Both`), `GeometricShapeSpec`, `ShapePersonalizationElement` e o construtor geométrico `ShapeGeometryBuilder` com discretização angular adaptativa de precisão laser (erro cordal `<= 0,02 mm`, `[36, 128]` segmentos) e hachura analítica a `45°` (`ENGRAVE_FILL`).
  - Integração completa com o motor de colisão SAT (`GeometryValidator.ValidatePanel`), exportação vetorial 1:1 para SVG, DXF R2000 (`AC1015`) e PDF 1.4 nas layers semânticas (`CUT_INTERNAL`, `ENGRAVE_VECTOR`, `ENGRAVE_FILL` e `MARK`), renderização 2D/3D em `OffscreenViewportRenderer` e persistência autocontida no formato `.cmaker` sem arquivos extras em `assets/`.
- **Desenho Interativo Click-and-Drag 2D, Guias de Snap e Badge de Cotas ao Vivo (Stage 23):**
  - Adicionado `Canvas2DToolMode` (`Select`, `DrawRectangle`, `DrawCircle`, `DrawSlot`, `DrawPolygon`, `DrawCrosshair`) em `SurfaceCanvas2DController` e `PersonalizationWorkspaceViewModel`, com *snapping* magnético (`1,5 mm`) para `(0, 0)`, centro da chapa e centros/bordas de elementos, modificadores `Shift` (quadrado perfeito) e `Alt` (desenho pelo centro), fallback para dimensões padrão em cliques curtos (`< 1,0 mm`) e cancelamento limpo via `Esc` ou botão direito.
  - Renderização das guias tracejadas `#00E5FF` e do **Badge Técnico de Cotas ao Vivo** (`#E60F172A` / `#38BDF8`) no passe de overlay preservando integralmente o bitmap base cacheado no `Editor2DStaticCache`.
  - Adição da faixa de ferramentas CAD no `Surface2DEditorControl`, seletor no `PersonalizationAreaLeftPanel` e atalhos protegidos (`V`, `R`, `C`, `S`, `P`, `X`, `Esc`) via `KeyboardShortcutGuard`.
- **Ferramentas CAD de Produtividade (`PersonalizationWorkspaceViewModel` & `PersonalizationAreaRightPanel`):**
  - Implementadas operações atômicas (1 passo de Undo/Redo e revalidação SAT `ValidatePanel`) de **Espelhamento nos 4 Cantos** (`MirrorFourCornersSelectedElement`) e **Grade Linear** (`CreateLinearArraySelectedElement` com `Qtd Total`, `Passo X` e `Passo Y`).

---

## [2.0.1] - 2026-11-14

### Adicionado / Melhorado (v2.0.1 — Splash Screen Nativa + Padronização Institucional IFMaker)
- **Splash Screen Profissional Nativa WinUI 3 (`SplashWindow` & `SplashScreenControl`):**
  - Janela inicial sem bordas (`740 × 440 px` lógicos, centralizada no monitor principal com suporte a DPI scaling) apresentando renderização isométrica 3D de alta precisão em SkiaSharp (`caixa com tampa aberta a 35°` e juntas finger joint sobre grade técnica), logomarcas institucionais oficiais (`Caixa Maker`, `IFMaker do Juruá`, `IFAC`), badges técnicos e barra de progresso em tempo real.
  - Pipeline real de pré-aquecimento assíncrono em 5 estágios durante a inicialização (`UiSettingsService`, compilação JIT dos motores `BoxModelRegistry`/`BoxScene3DBuilder`/`GeometryValidator`, pré-renderização offscreen em cache MTA das miniaturas 3D no `CatalogPreview3DService`, indexação da biblioteca `.cmaker` no `ProjectLibraryService` e transição suave para a `MainWindow`).
- **Padronização Institucional vs. Autoria Técnica (`PddroGCruz`):**
  - Definido oficialmente `"Laboratório IFMaker do Juruá — IFAC"` como `Publisher` / `Manufacturer` nos instaladores NSIS/WiX e metadados `.NET`, mantendo `"Pedro G. Cruz (PddroGCruz)"` nos créditos de engenharia e metadados de autoria/comentários.
  - Corrigida de forma consistente a grafia do identificador `PddroGCruz` no `AppVersion`, metadados `.NET`, instaladores, modal `Sobre / Instituição` e suíte de testes, e simplificado o cartão lateral da Central de Trabalho para foco estrito no vínculo institucional (`Laboratório IFMaker do Juruá • IFAC`).

---

## [2.0.0] - 2026-11-14

### Lançamento Major (v2.0.0 — Aplicação Nativa .NET 8 + WinUI 3 + SkiaSharp)
- **Promoção Oficial para `v2.0.0` (SemVer 2.0.0):**
  - Consolidação integral da arquitetura 100% nativa Windows (`CaixaMaker.Core`, `CaixaMaker.Cam`, `CaixaMaker.Presentation` e `CaixaMaker.App`), sem dependências externas de Python ou WebView2.
  - Inclui todo o pipeline CAD/CAM de alto desempenho desenvolvido ao longo das Etapas 1.5–1.10: visualizador 3D Z-Buffer paralelo SSAA com homografia analítica, cotas 3D clicáveis inline, prévia 3D no catálogo, mesa CNC customizável com nesting 1:1 em tempo real (`60+ FPS`) e pente de calibração de kerf expandido.
- **Autoria Oficial & Metadados:**
  - Adição oficial dos créditos do desenvolvedor **Pedro G. Cruz (`pddrogcruz` • `ggcostcr@gmail.com`)** na interface WinUI 3 (`CatalogHomeView`, modal `Sobre` e aba `Instituição`), metadados `.NET` (`Directory.Build.props`) e instaladores NSIS/WiX (`v2.0.0`).
  - Atualização da sincronização e auditoria centralizada em `scripts/version_manager.py` cobrindo também `app.manifest`, `CaixaMaker.nsi` e `Product.wxs`.

---

## [1.2.0] - 2026-11-14

### Alterado / Redesenhado (Etapa 1.5 — Identidade Visual e Experiência Desktop CAD/CAM)
- **Sistema de Design Técnico e Superfícies Grafite/Engenharia (`frontend/src/styles/index.css`):**
  - Substituída a paleta escura esverdeada por uma hierarquia de **grafite neutro multi-nível** (`#181A1D` fundo, `#23262B` painéis acoplados, `#2B2F36` controles elevados, `#363B44` divisores técnicos de `1px`) e um tema claro técnico de alto contraste (`#F0F2F5`, `#F8F9FB`, `#FFFFFF`, `#D8DEE6`), reservando o verde institucional (`#2E7D32` / `#4CAF50`) estritamente para acentos funcionais, seleção, foco e ações primárias.
- **Arquitetura de Painéis Acoplados e Protagonismo do Viewport 3D (`WorkspaceSplitShell.tsx`, `BoxViewer3D.tsx`):**
  - Eliminado o aspecto de "dashboard web" com cartões flutuantes e margens externas; os painéis laterais esquerdo e direito agora operam como **painéis técnicos acoplados** com cabeçalhos compactos (`32px`) e divisores verticais redimensionáveis de `1px`.
  - O `BoxViewer3D.tsx` passa a ocupar 100% da região central sem barras volumosas no topo, incorporando **HUDs técnicos flutuantes**: indicador de eixos 3D (`X/Y/Z`), seletor de vistas ortográficas/isométricas no canto superior direito e barra de controle flutuante inferior (`Abrir/Fechar tampa`, `Peças separadas`, `Mostrar dimensões`, `Restaurar visualização`).
- **Barra de Comando e Ribbon Contextual de Engenharia (`Header.tsx`, `Footer.tsx`):**
  - Cabeçalho reestruturado em duas faixas compactas de software desktop CAD/CAM: barra superior de comando com abas de área de trabalho (`Modelagem`, `Personalização`, `Fabricação`) e barra de ferramentas contextual agrupada (`Projeto`, `Histórico`, `Ferramentas CAD/CNC`, `Visualização e Fabricação`).
  - Barra de status técnica compacta (`28px`) com indicador operacional (`Pronto` / `Processando` / `Atenção CNC`), unidades (`mm`), modelo, material, dimensões, peças, validação CNC, projeto e versão (`v1.2.0`).
- **Central de Trabalho Inicial (`CatalogHomePage.tsx`, `BoxModelCard.tsx`, `ModelCardPreview3D.tsx`):**
  - Catálogo inicial transformado em **Central de Trabalho CAD/CAM** com barra lateral esquerda de navegação (`Início`, `Projetos Salvos`, `Modelos Paramétricos`, `Importar Arquivo`, `Sobre & Atalhos`), bloco institucional discreto e equilibrado (`IFMaker do Juruá` e `IFAC`), painel de ações rápidas (`Novo Projeto`, `Abrir projeto salvo`, `Importar arquivo`) e grade técnica de modelos 3D interativos.
- **Refinamento Técnico dos Painéis de Modelagem, Personalização e Fabricação:**
  - `ModelingLeftPanel.tsx` e `ParameterControl.tsx`: grupos técnicos compactos com seletor rápido de espessuras comerciais de MDF (`3`, `4`, `6`, `9 mm`), campos numéricos monoespaçados precisos e botão `Atualizar Modelo`.
  - `ModelingRightPanel.tsx`: inspetor estruturado em `Validação CNC` (checklist técnico), `Informações do Projeto` (dimensões externas/internas, área de corte estimada, largura de rasgos e dentes Finger Joint) e lista interativa `Peças (N)` com destaque no 3D.
  - `PersonalizationLeftPanel.tsx`, `Surface2DEditor.tsx`, `FabricationLeftPanel.tsx` e `FabricationRightPanel.tsx`: alinhados à nova linguagem gráfica CAD/CAM, com indicação explícita de escala vetorial `1:1 • mm` e distinção rigorosa entre erros bloqueantes, avisos e estado pronto para fabricação.

---

## [1.1.0] - 2026-11-14

### Adicionado
- **Barra de Menus Nativa do Windows (`tauri::menu`):**
  - Implementada barra de menus nativa Win32 no Tauri 2 (`frontend/src-tauri/src/lib.rs`) com os 7 menus completos: `Arquivo`, `Editar`, `Exibir`, `Projeto`, `Ferramentas`, `Janela` e `Ajuda`.
  - Sincronização bidirecional em tempo real entre o estado React/Zustand e a barra de menus nativa (`sync_desktop_ui_state`): habilitação/desabilitação dinâmica de itens (`Salvar`, `Desfazer`, `Refazer`, `Recortar`, `Copiar`, `Colar`, `Duplicar`, `Excluir`, exportações), indicadores de seleção (`CheckMenuItem`) para áreas de trabalho, modos de visualização (`3D`, `Dividido 3D+2D`, `2D`), painéis laterais, barra de ferramentas, cotas 3D, peças separadas, modelo ativo e tema (`Claro`, `Escuro`, `Automático`), além do submenu dinâmico `Abrir projeto recente`.
- **Barra de Ferramentas de Engenharia (`DesktopToolbar` em `Header.tsx`):**
  - Barra horizontal compacta abaixo da barra de cabeçalho/menus, dividida em 5 grupos funcionais (`Projeto`, `Histórico`, `Seleção e edição 2D`, `Visualização 3D e painéis` e `Áreas de trabalho`), com ícones Lucide, tooltips com atalhos e alternância de visibilidade persistente (`Exibir > Mostrar barra de ferramentas`).
- **Barra de Título Dinâmica e Barra de Status Contextual (`Footer.tsx`):**
  - Atualização dinâmica do título nativo da janela do Windows (`Caixa Maker — Projeto: <Nome> *`, `Caixa Maker — <Modelo> *` ou `Caixa Maker — Catálogo de Modelos`) e sincronização de tema nativo da janela (`window.set_theme`).
  - Barra de status inferior técnica exibindo em tempo real: modelo ativo, espessura do MDF (`mm`), dimensões externas (`L × P × A mm`), quantidade de peças, elementos CNC e status da validação de fabricação, estado de salvamento e conexão com o backend (`v1.1.0`).
- **Diálogos Nativos de Importação e Exportação de Projetos (`.caixamaker.json`):**
  - Novos endpoints `POST /api/projects/export-bundle` e `POST /api/projects/import-bundle` (`backend/personalization_api.py`) e integração com `@tauri-apps/plugin-dialog` (`pickProjectBundleViaDesktop`, `saveProjectBundleViaDesktop`) para importar e exportar pacotes completos de projeto (`.caixamaker.json`), preservando parâmetros, estado 3D/2D e elementos gráficos (incluindo imagens rasterizadas).
- **Novos Diálogos e Menus de Contexto Desktop:**
  - `ProjectPropertiesModal.tsx` (`Ctrl + I` / `Projeto > Informações e propriedades do projeto...`).
  - `PreferencesModal.tsx` (`Ctrl + ,` / `Ferramentas > Preferências e configurações...`).
  - `AboutModal.tsx` expandido com abas dedicadas (`Visão Geral`, `Atalhos de Teclado`, `Versão e Arquitetura`, `Créditos Institucionais`).
  - Menu de contexto (clique direito) no editor 2D (`Surface2DEditor.tsx`) e área de transferência interna (`Copiar`, `Recortar`, `Colar`, `Duplicar`, `Centralizar na face`, `Excluir`).
- **Sistema Centralizado de Atalhos de Teclado (`keyboardShortcuts.ts`):**
  - Suporte completo a `Ctrl+N`, `Ctrl+O`, `Ctrl+S`, `Ctrl+Shift+S`, `Ctrl+Shift+O`, `Ctrl+Shift+E`, `Ctrl+W`, `Ctrl+Z`, `Ctrl+Shift+Z`, `Ctrl+Y`, `Ctrl+X`, `Ctrl+C`, `Ctrl+V`, `Ctrl+D`, `Delete`, `Ctrl+A`, `Ctrl+B`, `Ctrl+Alt+B`, `Ctrl+Shift+T`, `Ctrl+I`, `Ctrl+,`, `Alt+1/2/3`, `Home`, `F1`, `F5` e `F11`, respeitando campos editáveis de texto.

---

## [1.0.1] - 2026-11-14

### Corrigido
- **Editor 2D — Tecla `Delete` e exclusão de elementos:**
  - Corrigida incompatibilidade de contrato HTTP entre `DELETE /api/personalization/projects/{project_id}/elements/{element_id}` (backend retornava `removed_id`) e o schema Zod `deletePersonalizationElement` no frontend (exigia `removed_element_id`), que impedia a remoção visual e local mesmo após resposta HTTP `200`, enquanto o snapshot empurrado antes da chamada permitia desfazer com `Ctrl + Z`.
  - Centralizado o tratamento de atalhos em `frontend/src/utils/keyboardShortcuts.ts`, ignorando repetições de tecla (`e.repeat`) e mantendo bloqueio estrito quando o foco está em campos textuais (`<input>` de texto/número, `<textarea>` e `[contenteditable]`), sem bloquear após foco em checkboxes, `<select>` ou alças de manipulação no editor 2D.
  - Garantido foco seguro no `Surface2DEditor.tsx` ao clicar/arrastar o elemento, alça de rotação ou alças de redimensionamento, limpando o foco de campos numéricos pendentes e integrando com `usePersonalizationStore.removeElement` (registro atômico em `pastSnapshots` somente após confirmação da API, atualização imediata em 2D e 3D, `hasUnsavedChanges = true`, rascunho local, `Ctrl + Z` e `Ctrl + Shift + Z` / `Ctrl + Y`).
- **Consolidação da correção anterior na versão Windows (DXF e projetos salvos):**
  - Preservadas e consolidadas nesta release `1.0.1` as correções previamente aplicadas ao conversor DXF (`_estimate_curve_length` e subdivisão de curvas em `exporters/vector_converter.py`), timeouts desktop (`api.ts`, `desktopBridge.ts`) e proteção contra condição de corrida na abertura de projetos salvos (`usePersonalizationStore.openProject`).

### Adicionado
- **Política permanente e automatizada de versionamento:**
  - Arquivo central `VERSION` na raiz do repositório como fonte única da versão.
  - Utilitário `scripts/version_manager.py` (`--current`, `--sync`, `--bump`, `--check`) integrado ao `scripts/build_desktop_release.ps1` e aos testes automatizados.
  - Injeção automática de `__APP_VERSION__` no frontend via `frontend/vite.config.ts` e leitura dinâmica em `backend/app_version.py` (`/api/health` e metadados FastAPI), mantendo sincronizados `package.json`, `tauri.conf.json`, `Cargo.toml`, UI (Header, Footer, Modal Sobre, Catálogo), executável Windows (PE VersionInfo) e instaladores NSIS/MSI.

---

## [1.0.0] - 2026-11-13

### Inicial
- Lançamento inicial consolidado do Caixa Maker (IFMaker do Juruá / IFAC — Campus Cruzeiro do Sul):
  - Modelagem paramétrica multi-modelo (`Caixa com Tampa e Alças` e `Caixa Aberta`).
  - Editor 2D e prévia 3D em tempo real com Three.js para textos, SVG vetorial e imagens rasterizadas com preparo CNC.
  - Exportação vetorial em escala real 1:1 para SVG, DXF R2000 (mm) e PDF, além de empacotamento desktop nativo Windows com Tauri 2 + sidecar Python.
