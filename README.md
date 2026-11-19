# Caixa Maker `v2.4.0` — CAD/CAM Paramétrico 1:1 para Windows

Aplicação desktop **100% nativa para Windows 10/11 (x64)** desenvolvida em **`.NET 8.0` + `Windows App SDK / WinUI 3` + `SkiaSharp`**, projetada para modelagem estrutural paramétrica em escala real (`1:1` em milímetros) de caixas em MDF/compensado com encaixes *Finger Joint*, personalização vetorial/raster CNC, otimização de corte 2D (*True-Shape Nesting*) e geração de planos de corte prontos para cortadoras a laser e routers CNC.

Vinculado ao **Laboratório IFMaker do Juruá — IFAC** (*Instituto Federal do Acre — Campus Cruzeiro do Sul • Programa PIBIT*).

---

## 1. Principais Recursos (`v2.4.0`)

### Fluxo de Trabalho Guiado em 3 Etapas (`1 → 2 → 3`)
- **Stepper Sequencial no Cabeçalho:** navegação guiada entre **1 Modelagem → 2 Personalização → 3 Fabricação**, com indicador visual de conclusão (`✓`) das etapas ultrapassadas e compatibilidade integral com os atalhos `Alt+1`, `Alt+2` e `Alt+3`.
- **Dicas Dinâmicas e Rodapés Fixos Contextuais:** orientação em tempo real na barra de status (`WorkflowGuidanceHint`) e botões fixos fora da área de rolagem nos painéis direitos (`Avançar para Personalização →`, `Pular direto para Fabricação »`, `← Modelagem`, `Avançar para Fabricação →` com alerta SAT não bloqueante e `← Voltar para Personalização`).

### Modelagem Paramétrica 1:1 & Viewport 3D em Tempo Real
- **Modelos Paramétricos Extensíveis:** **Caixa com Tampa Articulada e Alças** (6 chapas com dobradiça e pinos pivotantes) e **Caixa Aberta** (5 chapas sem tampa e sem alças).
- **Engenharia Dimensional Real e Encaixes *Finger Joint* Adaptativos:** alternância bidirecional exata entre medidas **Externas Totais** e **Internas Úteis**, compensação precisa de *kerf* (`folga` positiva ou negativa nos encaixes), **dimensionamento adaptativo de encaixes (*Auto / Tamanho mm / Qtd.*)** proporcional à espessura do MDF e ao comprimento útil de cada aresta vertical e da base, árvore interativa de chapas e **avaliador de expressões matemáticas** nos campos numéricos (`+`, `-`, `*`, `/`, parênteses).
- **Motor 3D SkiaSharp de Alta Taxa de Quadros:** Scanline Z-Buffer paralelo com isolamento STA/MTA + supersampling `SSAA 2×`, malhas trianguladas com cache *zero-copy*, abertura interativa da tampa (`0°` a `110°`), vista explodida, 6 vistas pré-definidas e edição direta ao clicar sobre as cotas 3D.

### Personalização CAD/CAM 2D Completa
- **Textos, SVGs e Imagens Raster:** vetorização de fontes TrueType/OpenType do sistema (contorno, preenchimento sólido e hachura cruzada a `45°`), importação sanitizada de arquivos `.svg` e processamento raster (PNG, JPG, BMP) com limiarização adaptativa e vetorização de contornos.
- **Primitivas Geométricas Paramétricas 1:1 (`mm`):** `Retângulo` (com raio de canto configurável), `Círculo / Furo calibrado`, `Oblongo (Slot)`, `Polígono regular` (`3` a `24` lados) e `Cruz de Centro`, com discretização adaptativa de precisão laser (erro cordal `<= 0,02 mm`) e modos de operação `Cut`, `Engrave` ou `Both`.
- **Desenho Interativo *Click-and-Drag* no Canvas 2D:** criação direta sobre a chapa com *snapping* magnético (`1,5 mm`) ao ponto `(0, 0)`, centro da chapa e eixos/bordas de elementos, trava de proporção (`Shift`), criação centrada (`Alt`), atalhos protegidos (`V`, `R`, `C`, `S`, `P`, `X`, `Esc`) e **Badge Técnico de Cotas ao Vivo** mantendo 60+ FPS sobre o bitmap cacheado.
- **Seleção Múltipla Ordenada, Alinhamento Estilo Inkscape e Repetição CAD:** seleção via `Shift + Clique`, caixa de seleção (*marquee*) e `Ctrl+A` com âncora `1º` preservada na pilha Undo/Redo; painel **Alinhar & Distribuir** relativo à *Chapa (Área Útil)*, *Primeiro Selecionado (`1º`)*, *Último Selecionado* ou *Caixa de Seleção* (6 eixos de alinhamento + distribuição horizontal/vertical); além de **Espelhamento nos 4 Cantos** e **Grade Linear** (`Qtd`, `Passo X`, `Passo Y`) com Undo/Redo atômico.

### Auditoria Pré-Corte SAT & Exportação CNC 1:1
- **Validação Geométrica via Teorema do Eixo Separador (SAT):** detecção incremental por chapa (`ValidatePanel`) contra invasão de bordas úteis, zonas *Finger Joint*, orelhas de dobradiça, furos estruturais e sobreposição entre elementos.
- **Exportação Vetorial 1:1 (`mm`):** geração em **SVG**, **DXF AutoCAD R2000 (`AC1015`)** e **PDF 1.4 vetorial** organizados nas **6 layers semânticas**:
  - `CUT_EXTERNAL` (contornos externos e dentes *Finger Joint*)
  - `CUT_INTERNAL` (rasgos passantes, furos estruturais e recortes internos)
  - `ENGRAVE_VECTOR` (textos e traços vetoriais de gravação)
  - `ENGRAVE_FILL` (preenchimentos sólidos e hachuras `45°`)
  - `MARK` (linhas e cruzes de marcação superficial)
  - `ENGRAVE_RASTER` (imagens bitmap preparadas para varredura a laser)
- **Mesa Laser, Pente de Kerf e Pacote `.ZIP`:** gabarito configurável da mesa de corte (presets e medida manual), **Pente de Calibração de Kerf** configurável (`1` a `21` rasgos) e exportação completa em `.ZIP` incluindo cópia editável `.cmaker`.

### Otimizador de Corte 2D (*True-Shape Nesting*)
- **Uso Independente (Catálogo) ou Integrado (Etapa 3):** acesse o otimizador diretamente pela Central de Projetos para organizar arquivos **`.svg` e `.dxf` externos** em retalhos de MDF, ou envie instantaneamente em memória todas as chapas da caixa ativa (com encaixes *Finger Joint* e personalizações acopladas) a partir da Etapa 3 de Fabricação — inclusive multiplicando o lote de caixas e misturando peças externas na mesma fila.
- **Motor *True-Shape* Multi-Estratégia:** aproveitamento real de concavidades (peças em L, recortes, dentes), encaixe opcional de peças menores dentro de furos internos grandes, controle de folga mínima entre cortes (`PartSpacingMm`), margem de segurança do retalho, políticas de rotação (`0°` travado para direção de veio, `180°`, `90°`, `45°`) e distribuição automática multi-chapa com viewport SkiaSharp a 60+ FPS.
- **Exportação 1:1 por Chapa e Lote `.ZIP`:** exportação individual da chapa exibida em **SVG**, **DXF R2000** ou **PDF 1.4**, além de pacote `.ZIP` completo com todas as chapas e relatório técnico de aproveitamento (`%`).

### Formato Nativo Autocontido `.cmaker`
- Pacote ZIP estruturado com `manifest.json` (validação criptográfica **SHA-256** por entrada e proteção estrita contra *Zip Slip*), parâmetros geométricos em `model.json`, elementos CNC por chapa, ativos SVG/raster embarcados em `assets/` com deduplicação por hash, miniaturas 3D/2D em `preview/` e associação nativa no Windows Explorer para abertura direta com duplo clique.

---

## 2. Arquitetura da Solution (`native-winui/CaixaMaker.sln`)

```text
gerador-de-caixas/
├── VERSION                                 # Fonte única de verdade SemVer (2.4.0)
├── CHANGELOG.md                            # Histórico oficial de versões
├── LICENSE                                 # Licença MIT
├── scripts/
│   ├── build_native_release.ps1            # Pipeline completo de release nativo (NSIS + WiX + SHA-256)
│   └── version_manager.py                  # Auditor SemVer dos manifestos nativos
└── native-winui/
    ├── CaixaMaker.sln                      # Solution .NET 8 (Core, Cam, Presentation, App, Tests)
    ├── Directory.Build.props               # Metadados centralizados de versão e compilação
    ├── installer/
    │   ├── nsis/                           # Instalador NSIS Unicode x64 em pt-BR
    │   └── wix/                            # Pacote corporativo MSI (WiX Toolset v3.14 pt-BR)
    ├── src/
    │   ├── CaixaMaker.Core/                # Domínio geométrico 1:1, modelos paramétricos, primitivas, SAT, nesting e storage
    │   ├── CaixaMaker.Cam/                 # Vetorização de texto/SVG/raster, importador DXF/SVG, nesting CAM e exportação SVG/DXF/PDF
    │   ├── CaixaMaker.Presentation/        # ViewModels MVVM, renderizadores 3D/2D SkiaSharp e pacotes .cmaker
    │   └── CaixaMaker.App/                 # Interface nativa WinUI 3, Splash Screen, Otimizador 2D, controles CAD e diálogos Windows
    └── tests/
        └── CaixaMaker.Tests/               # Suíte automatizada xUnit (360 testes aprovados)
```

---

## 3. Instalação para Usuários Finais (Windows 10/11 x64)

Os instaladores oficiais do Caixa Maker são **autocontidos (`win-x64`)**: já embarcam o runtime `.NET 8`, o `Windows App SDK` e o `SkiaSharp`, não exigindo instalação prévia de dependências externas.

1. Acesse a aba **Releases** do repositório no GitHub e baixe o instalador da versão **`v2.4.0`** de sua preferência:
   - **`CaixaMaker-Setup-2.4.0-x64.exe`** *(recomendado)* — instalador NSIS por usuário em português (`pt-BR`) com registro automático da extensão `.cmaker`.
   - **`CaixaMaker-2.4.0-x64.msi`** — instalador corporativo Windows Installer (WiX Toolset) em `pt-BR`.
2. Execute o instalador escolhido, conclua o assistente e abra o **Caixa Maker** pelo Menu Iniciar ou atalho da Área de Trabalho.

---

## 4. Compilação, Testes e Empacotamento a partir do Código-Fonte

### Pré-requisitos
- **Windows 10 / Windows 11 (x64)**
- **SDK `.NET 8.0`** instalado em `$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe` (com carga de trabalho Windows App SDK / Desktop .NET)
- *(Opcional, apenas para gerar os instaladores finais)* **NSIS (`makensis.exe`)** e **WiX Toolset v3.14 (`heat.exe`, `candle.exe`, `light.exe`)**

### Comandos Validados (PowerShell)

- **Compilação `Release x64`:**
  ```powershell
  & "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build native-winui/CaixaMaker.sln -c Release -p:Platform=x64
  ```

- **Execução Completa da Suíte xUnit (`360` testes):**
  ```powershell
  & "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test native-winui/CaixaMaker.sln -c Release -p:Platform=x64 --no-build
  ```

- **Smoke Test Headless WinUI 3 (`EXIT_CODE=0`):**
  ```powershell
  native-winui\src\CaixaMaker.App\bin\x64\Release\net8.0-windows10.0.19041.0\CaixaMaker.exe --smoke-test
  ```

- **Empacotamento Nativo Completo (Publicação `win-x64` + NSIS `.exe` + WiX `.msi` + SHA-256 em `release/`):**
  ```powershell
  powershell -ExecutionPolicy Bypass -File scripts\build_native_release.ps1
  ```

---

## 5. Autoria, Vinculação Institucional e Licença

- **Desenvolvimento e Arquitetura de Software:** `Pedro G. Cruz (PddroGCruz)` • `ggcostcr@gmail.com`
- **Instituição:** `Laboratório IFMaker do Juruá — Instituto Federal do Acre (IFAC) — Campus Cruzeiro do Sul • Programa PIBIT`
- **Licença:** Distribuído sob os termos da **Licença MIT** — consulte [LICENSE](LICENSE).
