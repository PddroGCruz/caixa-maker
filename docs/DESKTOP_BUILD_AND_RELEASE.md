# Guia de Build, Empacotamento e Distribuição Desktop (Windows) — Caixa Maker

Este documento descreve a arquitetura desktop do **Caixa Maker** e o fluxo reproduzível para compilar, testar e gerar os instaladores nativos para **Windows 10/11 (64 bits)**.

---

## 1. Visão Geral da Arquitetura Desktop

O aplicativo desktop empacota integralmente o frontend React/Three.js e o backend paramétrico Python em uma única distribuição autônoma sem exigir Python, Node.js, `npm` ou servidores externos na máquina do usuário final:

```text
[ Caixa Maker (Tauri 2 + WebView2 Nativo) ]
   ├── Frontend offline (dist/)
   ├── Bridge IPC Tauri (diálogos nativos Abrir/Salvar, single-instance, fechamento seguro)
   └── Gerenciador Rust (src-tauri/src/lib.rs)
         ├── Gera token local aleatório por sessão (X-CaixaMaker-Token)
         ├── Inicia sidecar Python oculto (CREATE_NO_WINDOW + Win32 Job Object KILL_ON_CLOSE)
         ├── Lê handshake stdout CAIXA_MAKER_READY { host, port, pid } em porta dinâmica (127.0.0.1:0)
         ├── Valida /api/health autenticado antes de liberar a interface principal
         └── Solicita encerramento limpo (/api/system/shutdown + fechamento de stdin) ao sair
```

### Garantias Implementadas
- **Fonte única de verdade preservada:** toda a modelagem (`models.py`, `box_models/`), personalização CNC (`personalization/`) e exportação CAD (`exporters/`) continua executando exclusivamente no backend Python.
- **Execução totalmente offline:** fontes remotas externas foram removidas do `index.html`; a tipografia utiliza a pilha nativa do Windows e `FontResolver` consulta `C:\Windows\Fonts`.
- **Segurança local:** servidor escuta exclusivamente em `127.0.0.1` com porta efêmera (`0`), valida cabeçalho `Host` contra *DNS rebinding*, exige `X-CaixaMaker-Token` nas rotas `/api/*` e aplica CORS restrito ao app local.
- **Zero processos órfãos:** combinação de encerramento gracioso (`/api/system/shutdown`), monitoramento de *pipe EOF* (`--watch-stdin`) e `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`.

---

## 2. Estrutura de Armazenamento no Windows

No modo desktop instalado (`%APPDATA%` e `%LOCALAPPDATA%`), os dados ficam separados dos binários do programa para evitar problemas de permissão em `Program Files` e preservar projetos do usuário em atualizações ou desinstalações:

| Finalidade | Diretório Windows | Observação |
| :--- | :--- | :--- |
| Projetos salvos (`.json`) | `%APPDATA%\CaixaMaker\projects\` | Preservado na desinstalação; recebe migração não-destrutiva de `./projects/*.json` legado na 1ª inicialização |
| Configurações futuras | `%APPDATA%\CaixaMaker\config\` | Reservado para preferências persistentes |
| Exportações temporárias | `%LOCALAPPDATA%\CaixaMaker\outputs\` | Limpeza automática de arquivos antigos (> 24 h) na inicialização |
| Arquivos de log | `%LOCALAPPDATA%\CaixaMaker\logs\` | `desktop-launcher.log` (Tauri/Rust) e `backend.log` (Python rotativo) |
| Temporários | `%LOCALAPPDATA%\CaixaMaker\temp\` | Limpo ao encerrar o aplicativo |

---

## 3. Pré-requisitos do Ambiente de Build

Na máquina de desenvolvimento responsável por gerar os pacotes de distribuição:

1. **Windows 10 ou Windows 11 (64 bits)**
2. **Python 3.11+** com ambiente virtual `.venv` configurado (`pip install -r backend\requirements.txt pyinstaller`)
3. **Node.js 20+** e **npm** com dependências instaladas em `frontend\` (`npm.cmd --prefix frontend install`)
4. **Rust MSVC (`stable-x86_64-pc-windows-msvc`)** em `%USERPROFILE%\.cargo\bin`
5. **Visual Studio Build Tools / Visual Studio C++ (x64)** + **Windows 10/11 SDK**

> **Nota:** Durante o primeiro `tauri build`, a CLI do Tauri baixa e armazena em cache local (`%LOCALAPPDATA%\tauri`) o compilador NSIS, o conjunto WiX Toolset (`wix314`) e o instalador offline oficial do Microsoft Edge WebView2 (`offlineInstaller`). Conexão com a internet é necessária apenas nessa etapa de build; o instalador NSIS gerado funciona offline no computador do usuário final.

---

## 4. Comandos de Build, Testes e Empacotamento

### 4.1. Build Completa Automatizada (Recomendado)

A partir da raiz do repositório:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build_desktop_release.ps1
```

Opções úteis do script:
- `-RegenerateIcons`: regera `desktop-icon-master.png`, `.ico` e todos os PNGs em `frontend\src-tauri\icons\` a partir de `frontend\public\assets\brand\optimized\caixa-maker-symbol.png`.
- `-SkipTests`: pula as suítes `unittest`, `vitest` e `cargo test` ao gerar builds rápidas iterativas.

### 4.2. Execução Etapa por Etapa (Manual)

1. **Gerar ícones desktop (opcional, caso o símbolo mude):**
   ```powershell
   .\.venv\Scripts\python.exe scripts\generate_desktop_icons.py
   ```

2. **Empacotar o sidecar Python (`--onedir`) e rodar o smoke test:**
   ```powershell
   .\.venv\Scripts\python.exe -m PyInstaller --noconfirm --clean scripts\caixa-maker-backend.spec --distpath frontend\src-tauri\resources --workpath build\pyinstaller
   .\frontend\src-tauri\resources\backend\caixa-maker-backend.exe --smoke-test
   ```

3. **Executar todas as suítes automatizadas:**
   ```powershell
   .\.venv\Scripts\python.exe -m unittest discover -s tests -v
   npm.cmd --prefix frontend test
   npm.cmd --prefix frontend run build
   cargo test --manifest-path frontend\src-tauri\Cargo.toml
   ```

4. **Compilar executável Tauri e instaladores NSIS/MSI:**
   ```powershell
   $env:PATH = "$env:USERPROFILE\.cargo\bin;$env:PATH"
   npm.cmd --prefix frontend run tauri build
   ```

---

## 5. Artefatos de Distribuição Gerados

Após executar `scripts\build_desktop_release.ps1`, os instaladores prontos para distribuição ficam reunidos na pasta `release\` (separada do código-fonte):

- `release\CaixaMaker-Setup.exe` — Instalador NSIS principal em Português do Brasil (atalho na Área de Trabalho, entrada no Menu Iniciar, escolha de diretório e desinstalador preservando `%APPDATA%\CaixaMaker\projects\`).
- `release\CaixaMaker-Setup-<versão>-x64.exe` — Cópia versionada do instalador NSIS para arquivamento de release.
- `release\CaixaMaker-<versão>-x64.msi` — Pacote Windows Installer (WiX) em Português do Brasil para implantação corporativa/GPO.

---

## 6. Versionamento Semântico Centralizado (`VERSION`)

A versão oficial reside no arquivo central `VERSION` na raiz do repositório e é propagada automaticamente para interface (`Vite` -> `APP_VERSION`), backend (`/api/health`), Rust/Tauri, metadados PE Windows e nomes dos instaladores. Consulte as regras detalhadas em [VERSIONING_POLICY.md](VERSIONING_POLICY.md).

Ao preparar uma nova release (`patch`, `minor` ou `major`):

```powershell
.\.venv\Scripts\python.exe scripts\version_manager.py --bump patch   # ou minor / major
# Em seguida, registre as mudanças em CHANGELOG.md e execute o build:
powershell -ExecutionPolicy Bypass -File scripts\build_desktop_release.ps1
```

Mantenha **inalterados** `bundle.windows.wix.upgradeCode` (`18839699-a044-5ba7-9322-34800fbe7ab1`) e `identifier` (`br.edu.ifac.ifmaker.caixamaker`) em `frontend/src-tauri/tauri.conf.json` para garantir atualização limpa sobre instalações anteriores.

---

## 7. Assinatura Digital e Windows SmartScreen

Os instaladores e executáveis gerados atualmente não possuem assinatura digital Authenticode comercial. Em distribuições internas/laboratoriais no Windows 10/11:
- O Windows Defender SmartScreen pode exibir o aviso padrão de aplicativo não reconhecido na primeira execução (*"Mais informações" → "Executar assim mesmo"*).
- Para eliminar o aviso em distribuição pública oficial do IFAC/IFMaker, basta configurar um certificado Authenticode válido (arquivo `.pfx` / HSM / Trusted Signing) em `frontend/src-tauri/tauri.conf.json` via `bundle.windows.certificateThumbprint` (e/ou `bundle.windows.signCommand`).
